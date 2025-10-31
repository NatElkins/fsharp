namespace FSharp.Compiler.HotReload

open System
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaEmitter

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

    /// <summary>
    /// Emits a delta for the supplied request; callers may commit the delta by invoking <see cref="OnDeltaApplied"/>.
    /// </summary>
    member _.EmitDelta(request: DeltaEmissionRequest) =
        match FSharp.Compiler.HotReloadState.tryGetSession() with
        | ValueNone -> Error HotReloadError.NoActiveSession
        | ValueSome session ->
            try
                let deltaRequest =
                    { IlxDeltaRequest.Baseline = session.Baseline
                      UpdatedTypes = request.UpdatedTypes
                      UpdatedMethods = request.UpdatedMethods
                      Module = request.IlModule
                      SymbolChanges = request.SymbolChanges
                      CurrentGeneration = session.CurrentGeneration
                      PreviousGenerationId = session.PreviousGenerationId }

                let delta = FSharp.Compiler.IlxDeltaEmitter.emitDelta deltaRequest
                Ok { Delta = delta }
            with ex ->
                Error(HotReloadError.DeltaEmissionException ex)

    /// <summary>Returns <c>true</c> if a hot reload session is active.</summary>
    member _.IsSessionActive =
        FSharp.Compiler.HotReloadState.tryGetSession().IsSome

    /// <summary>Convenience helper that both emits and commits a delta when the request succeeds.</summary>
    member this.EmitAndCommitDelta(request: DeltaEmissionRequest) =
        match this.EmitDelta(request) with
        | Ok result ->
            this.OnDeltaApplied(result.Delta.GenerationId)
            Ok result
        | Error error -> Error error

    /// <summary>Explicit commit hook mirroring Roslyn's service contract.</summary>
    member this.CommitPendingUpdate(generationId: Guid) =
        this.OnDeltaApplied(generationId)

    /// <summary>Explicit discard hook (no-op today, reserved for future bookkeeping).</summary>
    member _.DiscardPendingUpdate() =
        ()
