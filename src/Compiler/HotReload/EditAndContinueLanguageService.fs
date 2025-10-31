namespace FSharp.Compiler.HotReload

open System
open FSharp.Compiler.HotReloadBaseline

/// <summary>
/// Entry point mirroring Roslyn's <c>EditAndContinueLanguageService</c>. It centralises session lifecycle
/// management so callers do not talk to <see cref="HotReloadState"/> directly.
/// </summary>
type internal FSharpEditAndContinueLanguageService private () =

    static let lazyInstance = lazy FSharpEditAndContinueLanguageService()

    /// <summary>Singleton instance consumed by CLI and IDE hosts.</summary>
    static member Instance = lazyInstance.Value

    /// <summary>Initialise or replace the current baseline and reset the generation counters.</summary>
    member _.StartSession(baseline: FSharpEmitBaseline) =
        FSharp.Compiler.HotReloadState.setBaseline baseline

    /// <summary>Attempts to fetch the current baseline.</summary>
    member _.TryGetBaseline() =
        FSharp.Compiler.HotReloadState.tryGetBaseline()

    /// <summary>Attempts to fetch the current session (baseline + generation metadata).</summary>
    member _.TryGetSession() =
        FSharp.Compiler.HotReloadState.tryGetSession()

    /// <summary>Updates the stored EncId after a successful delta application.</summary>
    member _.OnDeltaApplied(generationId: Guid) =
        FSharp.Compiler.HotReloadState.recordDeltaApplied generationId

    /// <summary>Clears the session, typically when hot reload is disabled or the build finishes.</summary>
    member _.EndSession() =
        FSharp.Compiler.HotReloadState.clearBaseline()
