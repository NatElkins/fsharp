module FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateChild

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Runtime.Loader
open System.Diagnostics
open Xunit
open Xunit.Sdk
open Xunit.Sdk
open FSharp.Compiler.ComponentTests.HotReload
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.IlxDeltaEmitter

[<Fact>]
let ``ApplyUpdate child process`` () =
    let originalMessage = "Hello baseline"
    let updatedMessage = "Hello updated"

    printfn "[applyupdate-child] MetadataUpdater.IsSupported=%b" (MetadataUpdater.IsSupported)

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
    let baselineArtifacts = createBaselineFromRealCompiler baselineSource
    match DebuggerFlagProbe.tryComputeFlags baselineArtifacts.AssemblyPath with
    | Some flags -> printfn "[applyupdate-child] Debugger flags (computed)=%A" flags
    | None -> printfn "[applyupdate-child] Debugger flags (computed)=<unavailable>"

    let typeName = "Sample.MethodDemo"
    let methodKey = methodKeyByName baselineArtifacts.Baseline typeName "GetMessage"

    // Updated body emitted via IL helper (signature matches compiled baseline)
    let updatedModule = createMethodModule updatedMessage |> withDebuggableAttribute

    let request : IlxDeltaRequest =
        { Baseline = baselineArtifacts.Baseline
          UpdatedTypes = [ typeName ]
          UpdatedMethods = [ methodKey ]
          UpdatedAccessors = []
          Module = updatedModule
          SymbolChanges = None
          CurrentGeneration = 1
          PreviousGenerationId = None
          SynthesizedNames = None }

    let delta = emitDelta request

    // Load baseline into a fresh collectible ALC to avoid collisions.
    let alc = new AssemblyLoadContext("ApplyUpdateChild_" + Guid.NewGuid().ToString("N"), isCollectible = true)
    let assembly = alc.LoadFromAssemblyPath baselineArtifacts.AssemblyPath
    let sampleType = assembly.GetType(typeName, throwOnError = true)
    let method = sampleType.GetMethod("GetMessage", BindingFlags.Public ||| BindingFlags.Static)

    // Dump debugger bits and EnC capability
    let moduleType = assembly.ManifestModule.GetType()
    // Force-enable EnC by setting debugger bits: DACF_OBSOLETE_TRACK_JIT_INFO (0x4) | DACF_ENC_ENABLED (0x8)
    moduleType.GetMethod("SetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
    |> Option.ofObj
    |> Option.iter (fun m ->
        let paramType = m.GetParameters().[0].ParameterType
        let bitsObj = System.Enum.ToObject(paramType, 0x0C)
        m.Invoke(assembly.ManifestModule, [| bitsObj |]) |> ignore
        printfn "[applyupdate-child] SetDebuggerInfoBits invoked with 0x0C"
    )
    let dbgBits =
        moduleType.GetMethod("GetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
        |> ValueOption.ofObj
        |> ValueOption.map (fun m -> m.Invoke(assembly.ManifestModule, [||]) :?> int)
        |> ValueOption.orElseWith (fun () ->
            [ "m_debuggerInfoBits"; "m_debuggerBits" ]
            |> Seq.tryPick (fun name ->
                moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> Option.ofObj
                |> Option.map (fun f -> f.GetValue(assembly.ManifestModule) :?> int))
            |> ValueOption.ofOption)

    match dbgBits with
    | ValueSome bits -> printfn "[applyupdate-child] DebuggerInfoBits=0x%X" bits
    | ValueNone -> printfn "[applyupdate-child] DebuggerInfoBits: <unavailable>"
    assembly.GetCustomAttributes<DebuggableAttribute>()
    |> Seq.iter (fun a -> printfn "[applyupdate-child] Debuggable: tracking=%b disableOpt=%b modes=%A" a.IsJITTrackingEnabled a.IsJITOptimizerDisabled a.DebuggingFlags)
    try
        let moduleInfo = assembly.GetType("Sample.ModuleInfo", throwOnError = true)
        let bitsFromHelper = moduleInfo.GetMethod("TryGetDebuggerInfoBits", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let encCapableHelper = moduleInfo.GetMethod("TryIsEditAndContinueCapable", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let encEnabledHelper = moduleInfo.GetMethod("TryIsEditAndContinueEnabled", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        let peFlags = moduleInfo.GetMethod("TryPeFlags", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||])
        printfn "[applyupdate-child] DebuggerInfoBits(ModuleInfo)=%A" bitsFromHelper
        printfn "[applyupdate-child] ModuleInfo.TryIsEditAndContinueCapable=%A" encCapableHelper
        printfn "[applyupdate-child] ModuleInfo.TryIsEditAndContinueEnabled=%A" encEnabledHelper
        printfn "[applyupdate-child] ModuleInfo.TryPeFlags=%A" peFlags
    with ex ->
        printfn "[applyupdate-child] ModuleInfo helpers unavailable: %s" (ex.ToString())
    printfn "[applyupdate-child] AssemblyName=%s Path=%s" assembly.FullName assembly.Location
    [ "m_debuggerInfoBits"; "m_debuggerBits"; "m_dwTransientFlags" ]
    |> List.iter (fun name ->
        match moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic) with
        | null -> ()
        | f ->
            let value = f.GetValue(assembly.ManifestModule)
            printfn "[applyupdate-child] %s=%A" name value)

    let encMethod = moduleType.GetMethod("IsEditAndContinueCapable", BindingFlags.Instance ||| BindingFlags.NonPublic)
    let encCapable =
        match encMethod with
        | null ->
            printfn "[applyupdate-child] IsEditAndContinueCapable not found on %s" moduleType.FullName
            false
        | m ->
            let r = m.Invoke(assembly.ManifestModule, [||]) :?> bool
            printfn "[applyupdate-child] IsEditAndContinueCapable=%b" r
            r
    if not encCapable then
        printfn "[applyupdate-child] Skipping body: module not EnC-capable."
    else
        let before = method.Invoke(null, [||]) :?> string
        Assert.Equal(originalMessage, before)

        let pdbBytes =
            match delta.Pdb with
            | Some bytes -> bytes
            | None -> Array.empty

        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), pdbBytes.AsSpan())

        let after = method.Invoke(null, [||]) :?> string
        Assert.Equal(updatedMessage, after)
