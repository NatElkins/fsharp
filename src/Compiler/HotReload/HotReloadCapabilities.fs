namespace FSharp.Compiler.HotReload

open System
#if NET5_0_OR_GREATER
open System.Reflection.Metadata
#endif

[<Flags>]
type internal HotReloadCapabilityFlags =
    | None = 0
    | Il = 1
    | Metadata = 2
    | PortablePdb = 4
    | MultipleGenerations = 8
    | RuntimeApply = 16

type internal HotReloadCapabilities =
    { Flags: HotReloadCapabilityFlags }

module internal HotReloadCapability =

    let private runtimeApplySupported : bool =
#if NET5_0_OR_GREATER
        try
            MetadataUpdater.IsSupported
        with _ -> false
#else
        false
#endif

    let private runtimeApplyFeatureFlag : bool =
        match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY") with
        | null
        | "" -> false
        | value when value.Equals("1", StringComparison.OrdinalIgnoreCase) -> true
        | value when value.Equals("true", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false

    let private runtimeApplyEnabled = runtimeApplySupported && runtimeApplyFeatureFlag

    let current : HotReloadCapabilities =
        let baseFlags =
            HotReloadCapabilityFlags.Il
            ||| HotReloadCapabilityFlags.Metadata
            ||| HotReloadCapabilityFlags.PortablePdb
            ||| HotReloadCapabilityFlags.MultipleGenerations

        let flags =
            if runtimeApplyEnabled then
                baseFlags ||| HotReloadCapabilityFlags.RuntimeApply
            else
                baseFlags

        { Flags = flags }

    let supportsRuntimeApply = runtimeApplyEnabled
