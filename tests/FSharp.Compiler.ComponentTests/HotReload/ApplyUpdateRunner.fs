module FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateRunner

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Runtime.Loader
open System.Diagnostics
open Xunit
open Xunit.Sdk
open FSharp.Compiler.ComponentTests.HotReload
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.IlxDeltaEmitter

// This is a minimal console-style entry point that can be launched via `dotnet test --filter ...`
// to isolate hosting from vstest. It returns success if ApplyUpdate succeeds, otherwise throws.
[<Fact>]
let ``ApplyUpdate runner`` () =
    // Require EnC env set by parent process; fail fast if missing.
    let modifiable = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
    if not (String.Equals(modifiable, "debug", StringComparison.OrdinalIgnoreCase)) then
        failwith "DOTNET_MODIFIABLE_ASSEMBLIES must be 'debug' for this runner."

    printfn "[applyupdate-runner] MetadataUpdater.IsSupported=%b" (MetadataUpdater.IsSupported)

    // Build the baseline with the real compiler (Debug) so the runtime marks it EnC-capable.
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
                // CoreCLR marks IsSystem via PEAssembly::IsSystem; approximate: name == System.Private.CoreLib
                bool isSystem = string.Equals(md.GetString(asm.Name), "System.Private.CoreLib", StringComparison.Ordinal);
                bool isRefEmit = false; // Reflection.Emit not used here
                bool isR2R = pe.PEHeaders.CorHeader.Flags.HasFlag(System.Reflection.PortableExecutable.CorFlags.ILOnly) == false;
                return (isSystem, isRefEmit, isR2R);
            }
            catch { return null; }
        }
    }
}
"""
    let baselineArtifacts = createBaselineFromRealCompiler baselineSource

    let typeName = "Sample.MethodDemo"
    let updatedMessage = "Hello updated"
    let methodKey = methodKeyByName baselineArtifacts.Baseline typeName "GetMessage"
    // Updated body emitted via IL helper (method signature matches the compiled baseline type)
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

    // Load baseline into a non-collectible ALC to match CoreCLR EnC code paths (collectible modules may not be marked EnC-capable).
    let alc = new AssemblyLoadContext("ApplyUpdateRunner_" + Guid.NewGuid().ToString("N"), isCollectible = false)
    let assembly = alc.LoadFromAssemblyPath baselineArtifacts.AssemblyPath
    let moduleType = assembly.ManifestModule.GetType()
    // Force-enable EnC by calling the private SetDebuggerInfoBits with DACF_ENC_ENABLED and without DACF_ALLOW_JIT_OPTS
    moduleType.GetMethod("SetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
    |> Option.ofObj
    |> Option.iter (fun m ->
        let paramType = m.GetParameters().[0].ParameterType
        // Bits: DACF_OBSOLETE_TRACK_JIT_INFO (0x4) | DACF_ENC_ENABLED (0x8) => 0xC, leaves DACF_ALLOW_JIT_OPTS cleared
        let bitsObj = System.Enum.ToObject(paramType, 0x0C)
        m.Invoke(assembly.ManifestModule, [| bitsObj |]) |> ignore
        printfn "[applyupdate-runner] SetDebuggerInfoBits invoked with 0x0C"
    )
    // Inspect DebuggableAttribute via managed probe to mirror CoreCLR ComputeDebuggingConfig logic.
    match DebuggerFlagProbe.tryComputeFlags baselineArtifacts.AssemblyPath with
    | Some flags -> printfn "[applyupdate-runner] Debugger flags (computed) = %A" flags
    | None -> printfn "[applyupdate-runner] Debugger flags (computed) unavailable"
    // Dump debugger info bits via reflection if available
    let dbgBits =
        moduleType.GetMethod("GetDebuggerInfoBits", BindingFlags.Instance ||| BindingFlags.NonPublic)
        |> Option.ofObj
        |> Option.map (fun m -> m.Invoke(assembly.ManifestModule, [||]) :?> int)
        |> Option.orElseWith (fun () ->
            // Try known private field names seen in coreclr
            [ "m_debuggerInfoBits"; "m_debuggerBits" ]
            |> Seq.tryPick (fun name ->
                moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> Option.ofObj
                |> Option.map (fun f -> f.GetValue(assembly.ManifestModule) :?> int)))
    match dbgBits with
    | Some bits -> printfn "[applyupdate-runner] DebuggerInfoBits=0x%X" bits
    | None -> printfn "[applyupdate-runner] DebuggerInfoBits: <unavailable>"
    // Also call the helper inside the baseline assembly
    try
        let moduleInfo = assembly.GetType("Sample.ModuleInfo", throwOnError = true)
        let bitsFromHelper = moduleInfo.GetMethod("TryGetDebuggerInfoBits", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||]) :?> obj
        let encCapableHelper = moduleInfo.GetMethod("TryIsEditAndContinueCapable", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||]) :?> obj
        let encEnabledHelper = moduleInfo.GetMethod("TryIsEditAndContinueEnabled", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||]) :?> obj
        let peFlags = moduleInfo.GetMethod("TryPeFlags", BindingFlags.Public ||| BindingFlags.Static).Invoke(null, [||]) :?> obj
        printfn "[applyupdate-runner] DebuggerInfoBits(ModuleInfo)=%A" bitsFromHelper
        printfn "[applyupdate-runner] ModuleInfo.TryIsEditAndContinueCapable=%A" encCapableHelper
        printfn "[applyupdate-runner] ModuleInfo.TryIsEditAndContinueEnabled=%A" encEnabledHelper
        printfn "[applyupdate-runner] ModuleInfo.TryPeFlags=%A" peFlags
    with ex ->
        printfn "[applyupdate-runner] ModuleInfo helpers unavailable: %s" (ex.ToString())
    // Dump DebuggableAttribute flags for clarity
    assembly.GetCustomAttributes<DebuggableAttribute>()
    |> Seq.iter (fun a -> printfn "[applyupdate-runner] Debuggable: tracking=%b disableOpt=%b modes=%A" a.IsJITTrackingEnabled a.IsJITOptimizerDisabled a.DebuggingFlags)
    printfn "[applyupdate-runner] AssemblyName=%s Path=%s" assembly.FullName assembly.Location
    // Dump raw debugger flags stored in the module for EnC gating clues.
    [ "m_debuggerInfoBits"; "m_debuggerBits"; "m_dwTransientFlags" ]
    |> List.iter (fun name ->
        match moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic) with
        | null -> ()
        | f ->
            let value = f.GetValue(assembly.ManifestModule)
            printfn "[applyupdate-runner] %s=%A" name value)
    // Enumerate all instance fields to identify potential debugger flag storage names.
    moduleType.GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
    |> Array.iter (fun f -> printfn "[applyupdate-runner] module field: %s (%A)" f.Name f.FieldType)
    // Try assembly-level debugger flags (RuntimeAssembly.m_debuggerFlags).
    let asmType = assembly.GetType()
    match asmType.GetField("m_debuggerFlags", BindingFlags.Instance ||| BindingFlags.NonPublic) with
    | null -> ()
    | f ->
        let v = f.GetValue(assembly)
        printfn "[applyupdate-runner] assembly m_debuggerFlags=%A" v
    // Try to read raw debugger flags stored in the module
    [ "m_debuggerInfoBits"; "m_debuggerBits"; "m_dwTransientFlags" ]
    |> List.iter (fun name ->
        match moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic) with
        | null -> ()
        | f ->
            let value = f.GetValue(assembly.ManifestModule)
            printfn "[applyupdate-runner] %s=%A" name value)

    // Note: IsEditAndContinueCapable is a native method in CoreCLR (ceeload.cpp), not exposed in managed code.
    // We can't check it via reflection. Instead, just try ApplyUpdate - if the assembly isn't EnC-capable,
    // it will throw InvalidOperationException with "assembly not editable" message.

    let method = assembly.GetType(typeName, throwOnError = true).GetMethod("GetMessage", BindingFlags.Public ||| BindingFlags.Static)
    let before = method.Invoke(null, [||]) :?> string
    printfn "[applyupdate-runner] Before update: %s" before
    if before <> "Hello baseline" then failwithf "Unexpected baseline result: %s" before

    let pdbBytes =
        match delta.Pdb with
        | Some bytes -> bytes
        | None -> Array.empty

    printfn "[applyupdate-runner] Applying delta: metadata=%d bytes, IL=%d bytes, PDB=%d bytes"
        delta.Metadata.Length delta.IL.Length pdbBytes.Length

    // Dump delta to /tmp for analysis with mdv
    let dumpDir = "/tmp/fsharp-delta-debug"
    if not (Directory.Exists dumpDir) then Directory.CreateDirectory dumpDir |> ignore
    File.WriteAllBytes(Path.Combine(dumpDir, "1.meta"), delta.Metadata)
    File.WriteAllBytes(Path.Combine(dumpDir, "1.il"), delta.IL)
    if pdbBytes.Length > 0 then File.WriteAllBytes(Path.Combine(dumpDir, "1.pdb"), pdbBytes)
    File.Copy(baselineArtifacts.AssemblyPath, Path.Combine(dumpDir, "baseline.dll"), true)
    printfn "[applyupdate-runner] Delta written to %s" dumpDir

    try
        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), pdbBytes.AsSpan())
        printfn "[applyupdate-runner] ApplyUpdate succeeded!"
    with
    | :? InvalidOperationException as ex when ex.Message.Contains("not editable") ->
        failwithf "Assembly is NOT EnC-capable: %s" ex.Message
    | :? InvalidOperationException as ex ->
        // Re-throw with more context - this likely means delta is malformed
        failwithf "ApplyUpdate failed (assembly IS EnC-capable, but delta rejected): %s" ex.Message

    let after = method.Invoke(null, [||]) :?> string
    printfn "[applyupdate-runner] After update: %s" after
    if after <> updatedMessage then failwithf "Unexpected updated result: expected '%s' but got '%s'" updatedMessage after

    printfn "[applyupdate-runner] SUCCESS: Hot reload worked! Value changed from '%s' to '%s'" before after
