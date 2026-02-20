module FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateConsole

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Runtime.Loader
open Xunit
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateShared
open FSharp.Compiler.IlxDeltaEmitter

[<Literal>]
let private DotnetModifiableAssembliesEnvVar = "DOTNET_MODIFIABLE_ASSEMBLIES"

/// Not a real test; used via `dotnet test --filter ...` as a console-style host to avoid vstest reuse.
[<Fact>]
let ``ApplyUpdate console host`` () =
    if not (String.Equals(Environment.GetEnvironmentVariable(DotnetModifiableAssembliesEnvVar), "debug", StringComparison.OrdinalIgnoreCase)) then
        failwith $"{DotnetModifiableAssembliesEnvVar} must be 'debug' for this host."

    printfn "[applyupdate-console] MetadataUpdater.IsSupported=%b" (MetadataUpdater.IsSupported)

    // Baseline compiled with the real compiler (Debug) so the runtime sees EnC capability.
    let baselineSource = baselineSourceText
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
        |> ValueOption.ofObj
        |> ValueOption.map (fun m -> m.Invoke(assembly.ManifestModule, [||]) :?> int)
        |> ValueOption.orElseWith (fun () ->
            [ "m_debuggerInfoBits"; "m_debuggerBits" ]
            |> Seq.tryPick (fun name ->
                moduleType.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> Option.ofObj
                |> Option.map (fun f -> f.GetValue(assembly.ManifestModule) :?> int))
            |> ValueOption.ofOption)
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
