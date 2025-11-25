module FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateConsole

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Runtime.Loader
open Xunit
open Xunit.Sdk
open Xunit.Sdk
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.IlxDeltaEmitter

/// Not a real test; used via `dotnet test --filter ...` as a console-style host to avoid vstest reuse.
[<Fact>]
let ``ApplyUpdate console host`` () =
    if not (String.Equals(Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES"), "debug", StringComparison.OrdinalIgnoreCase)) then
        failwith "DOTNET_MODIFIABLE_ASSEMBLIES must be 'debug' for this host."

    printfn "[applyupdate-console] MetadataUpdater.IsSupported=%b" (MetadataUpdater.IsSupported)

    // Baseline compiled with the real compiler (Debug) so the runtime sees EnC capability.
    let baselineSource = """
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

[assembly: System.Diagnostics.Debuggable(System.Diagnostics.DebuggableAttribute.DebuggingModes.Default |
                                         System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations |
                                         System.Diagnostics.DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]

namespace Sample
{
    public static class MethodDemo
    {
        public static string GetMessage() => "Hello baseline";
    }

    public static class ModuleInfo
    {
        static partial class Accessors
        {
            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetDebuggerInfoBits")]
            public static extern int CallGetDebuggerInfoBits(Module module);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsEditAndContinueCapable")]
            public static extern bool CallIsEnCCapable(Module module);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsEditAndContinueEnabled")]
            public static extern bool CallIsEncEnabled(Module module);
        }

        public static int? TryGetDebuggerInfoBits()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallGetDebuggerInfoBits(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("GetDebuggerInfoBits", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null)
                return (int)m.Invoke(mod, null);
            var f = t.GetField("m_debuggerBits", BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? t.GetField("m_debuggerInfoBits", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
                return (int)f.GetValue(mod);
            return null;
        }

        public static bool? TryIsEditAndContinueCapable()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallIsEnCCapable(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("IsEditAndContinueCapable", BindingFlags.Instance | BindingFlags.NonPublic);
            return m != null ? (bool)m.Invoke(mod, null) : (bool?)null;
        }

        public static bool? TryIsEditAndContinueEnabled()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallIsEncEnabled(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("IsEditAndContinueEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            return m != null ? (bool)m.Invoke(mod, null) : (bool?)null;
        }

        public static (bool isSystem, bool isReflectionEmit, bool isReadyToRun)? TryPeFlags()
        {
            try
            {
                var path = typeof(ModuleInfo).Assembly.Location;
                using var fs = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                var md = pe.GetMetadataReader();
                var asm = md.GetAssemblyDefinition();
                bool isSystem = string.Equals(md.GetString(asm.Name), "System.Private.CoreLib", StringComparison.Ordinal);
                bool isRefEmit = false;
                bool isR2R = pe.PEHeaders.CorHeader.Flags.HasFlag(System.Reflection.PortableExecutable.CorFlags.ILOnly) == false;
                return (isSystem, isRefEmit, isR2R);
            }
            catch { return null; }
        }
    }
}
"""
    let baseline = createBaselineFromRealCompiler baselineSource
    match DebuggerFlagProbe.tryComputeFlags baseline.AssemblyPath with
    | Some flags -> printfn "[applyupdate-console] Debugger flags (computed)=%A" flags
    | None -> printfn "[applyupdate-console] Debugger flags (computed)=<unavailable>"
    let updatedModule = createMethodModule "Hello updated" |> withDebuggableAttribute
    let typeName = "Sample.MethodDemo"
    let methodKey = methodKeyByName baseline.Baseline typeName "GetMessage"

    let request : IlxDeltaRequest =
        { Baseline = baseline.Baseline
          UpdatedTypes = [ typeName ]
          UpdatedMethods = [ methodKey ]
          UpdatedAccessors = []
          Module = updatedModule
          SymbolChanges = None
          CurrentGeneration = 1
          PreviousGenerationId = None
          SynthesizedNames = None }

    let delta = emitDelta request

    let alc = new AssemblyLoadContext("ApplyUpdateConsole_" + Guid.NewGuid().ToString("N"), isCollectible = true)
    let assembly = alc.LoadFromAssemblyPath baseline.AssemblyPath
    let moduleType = assembly.ManifestModule.GetType()
    // Force-enable EnC by setting debugger bits: DACF_OBSOLETE_TRACK_JIT_INFO (0x4) | DACF_ENC_ENABLED (0x8)
    moduleType.GetMethod("SetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
    |> Option.ofObj
    |> Option.iter (fun m ->
        let paramType = m.GetParameters().[0].ParameterType
        let bitsObj = System.Enum.ToObject(paramType, 0x0C)
        m.Invoke(assembly.ManifestModule, [| bitsObj |]) |> ignore
        printfn "[applyupdate-console] SetDebuggerInfoBits invoked with 0x0C"
    )
    let dbgBits =
        moduleType.GetMethod("GetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
        |> Option.ofObj
        |> Option.map (fun m -> m.Invoke(assembly.ManifestModule, [||]) :?> int)
        |> Option.orElseWith (fun () ->
            [ "m_debuggerInfoBits"; "m_debuggerBits" ]
            |> Seq.tryPick (fun name ->
                moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> Option.ofObj
                |> Option.map (fun f -> f.GetValue(assembly.ManifestModule) :?> int)))
    printfn "[applyupdate-console] DebuggerInfoBits=%A" dbgBits

    // Call ModuleInfo helpers (unsafe accessors) for native flags
    try
        let moduleInfo = assembly.GetType("Sample.ModuleInfo", throwOnError = true)
        let bitsFromHelper = moduleInfo.GetMethod("TryGetDebuggerInfoBits", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let encCapableHelper = moduleInfo.GetMethod("TryIsEditAndContinueCapable", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let encEnabledHelper = moduleInfo.GetMethod("TryIsEditAndContinueEnabled", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let peFlags = moduleInfo.GetMethod("TryPeFlags", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        printfn "[applyupdate-console] DebuggerInfoBits(ModuleInfo)=%A" bitsFromHelper
        printfn "[applyupdate-console] ModuleInfo.TryIsEditAndContinueCapable=%A" encCapableHelper
        printfn "[applyupdate-console] ModuleInfo.TryIsEditAndContinueEnabled=%A" encEnabledHelper
        printfn "[applyupdate-console] ModuleInfo.TryPeFlags=%A" peFlags
    with ex ->
        printfn "[applyupdate-console] ModuleInfo helpers unavailable: %s" (ex.ToString())

    assembly.GetCustomAttributes()
    |> Seq.filter (fun a -> a.GetType().Name = "DebuggableAttribute")
    |> Seq.iter (fun a -> printfn "[applyupdate-console] Debuggable attr=%A" a)

    let encMethod = moduleType.GetMethod("IsEditAndContinueCapable", BindingFlags.Instance ||| BindingFlags.NonPublic)
    let encCapable =
        match encMethod with
        | null ->
            printfn "[applyupdate-console] IsEditAndContinueCapable not found on %s" moduleType.FullName
            false
        | m ->
            let r = m.Invoke(assembly.ManifestModule, [||]) :?> bool
            printfn "[applyupdate-console] IsEditAndContinueCapable=%b" r
            r
    printfn "[applyupdate-console] IsEnCCapable=%b" encCapable
    if not encCapable then
        printfn "[applyupdate-console] Skipping ApplyUpdate: module not EnC-capable."
        ()
    else
        let sampleType = assembly.GetType(typeName, throwOnError = true)
        let method = sampleType.GetMethod("GetMessage", BindingFlags.Public ||| BindingFlags.Static)
        let before = method.Invoke(null, [||]) :?> string
        printfn "[applyupdate-console] before=%s" before

        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), (defaultArg delta.Pdb Array.empty).AsSpan())

        let after = method.Invoke(null, [||]) :?> string
        printfn "[applyupdate-console] after=%s" after
        if after <> "Hello updated" then failwith "ApplyUpdate did not apply."
