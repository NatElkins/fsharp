namespace FSharp.Compiler.HotReload

open System
open System.IO
open FSharp.Compiler
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.HotReload.DeltaBuilder
open FSharp.Compiler.TypedTree
open FSharp.Compiler.SynthesizedTypeMaps

/// <summary>
/// Entry point mirroring Roslyn's <c>EditAndContinueLanguageService</c>. It centralises session lifecycle
/// management so callers do not talk to <see cref="HotReloadState"/> directly.
/// </summary>
type internal FSharpEditAndContinueLanguageService private () =

    static let lazyInstance = lazy FSharpEditAndContinueLanguageService()
    /// Lock to coordinate updates between HotReloadState.session and lastBaselineState
    static let stateLock = obj ()
    static let mutable lastBaselineState : (FSharpEmitBaseline * CheckedAssemblyAfterOptimization) option = None
    static let shouldTraceMetadata () =
        match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METADATA") with
        | null -> false
        | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
        | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false
    static let createSynthesizedMapFromSnapshot (snapshot: Map<string, string[]>) =
        let map = FSharpSynthesizedTypeMaps()
        map.LoadSnapshot(snapshot |> Map.toSeq)
        map.BeginSession()
        map

    /// <summary>Singleton instance consumed by CLI and IDE hosts.</summary>
    static member Instance = lazyInstance.Value

    /// <summary>Initialise or replace the current baseline and reset the generation counters.</summary>
    member _.StartSession(baseline: FSharpEmitBaseline) =
        use _ =
            Activity.start "HotReload.StartSession" [|
                Activity.Tags.project, baseline.ModuleId.ToString()
                Activity.Tags.hotReloadAction, "baseline"
            |]

        lock stateLock (fun () ->
            FSharp.Compiler.HotReloadState.setBaseline baseline (CheckedAssemblyAfterOptimization [])
            lastBaselineState <- Some(baseline, CheckedAssemblyAfterOptimization []))

    member _.StartSession(baseline: FSharpEmitBaseline, implementationFiles: CheckedAssemblyAfterOptimization) =
        use _ =
            Activity.start "HotReload.StartSession" [|
                Activity.Tags.project, baseline.ModuleId.ToString()
                Activity.Tags.hotReloadAction, "baseline+impl"
            |]

        lock stateLock (fun () ->
            FSharp.Compiler.HotReloadState.setBaseline baseline implementationFiles
            lastBaselineState <- Some(baseline, implementationFiles))

    /// <summary>Attempts to fetch the current baseline.</summary>
    member _.TryGetBaseline() =
        FSharp.Compiler.HotReloadState.tryGetBaseline()

    /// <summary>Attempts to fetch the current session (baseline + generation metadata).</summary>
    member _.TryGetSession() =
        FSharp.Compiler.HotReloadState.tryGetSession()

    /// <summary>Updates the stored EncId after a successful delta application.</summary>
    member _.OnDeltaApplied(generationId: Guid) =
        lock stateLock (fun () ->
            FSharp.Compiler.HotReloadState.recordDeltaApplied generationId
            match FSharp.Compiler.HotReloadState.tryGetSession() with
            | ValueSome session -> lastBaselineState <- Some(session.Baseline, session.ImplementationFiles)
            | ValueNone -> ())

    /// <summary>Clears the session, typically when hot reload is disabled or the build finishes.</summary>
    member _.EndSession() =
        FSharp.Compiler.HotReloadState.clearBaseline()

    /// <summary>
    /// Emits a delta for the supplied request; callers may commit the delta by invoking <see cref="OnDeltaApplied"/>.
    /// </summary>
    member _.EmitDelta(request: DeltaEmissionRequest) =
        let trace = shouldTraceMetadata ()
        if trace then
            let asm = typeof<FSharpEditAndContinueLanguageService>.Assembly
            let message = sprintf "[fsharp-hotreload][service] EmitDelta invoked (assembly=%s)\n" asm.Location
            printf "%s" message
            try
                let path = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-service.log")
                File.AppendAllText(path, message)
            with :? IOException as ex ->
                eprintfn "[fsharp-hotreload][service] Failed to write trace log: %s" ex.Message
        match FSharp.Compiler.HotReloadState.tryGetSession() with
        | ValueNone -> Error HotReloadError.NoActiveSession
        | ValueSome session ->
            use _ =
                Activity.start "HotReload.EmitDelta" [|
                    Activity.Tags.generation, string session.CurrentGeneration
                    Activity.Tags.project, session.Baseline.ModuleId.ToString()
                |]
            try
                if trace then
                    printfn
                        "[fsharp-hotreload][service] session prev=%A baselineEncId=%O"
                        session.PreviousGenerationId
                        session.Baseline.EncId

                let synthesizedMap = createSynthesizedMapFromSnapshot session.Baseline.SynthesizedNameSnapshot

                let deltaRequest =
                    { IlxDeltaRequest.Baseline = session.Baseline
                      UpdatedTypes = request.UpdatedTypes
                      UpdatedMethods = request.UpdatedMethods
                      UpdatedAccessors = request.UpdatedAccessors
                      Module = request.IlModule
                      SymbolChanges = request.SymbolChanges
                      CurrentGeneration = session.CurrentGeneration
                      PreviousGenerationId = session.PreviousGenerationId
                      SynthesizedNames = Some synthesizedMap }

                let delta = FSharp.Compiler.IlxDeltaEmitter.emitDelta deltaRequest
                if trace then
                    let line = sprintf "[fsharp-hotreload][service] EmitDelta produced encLog=%A\n" delta.EncLog
                    printf "%s" line
                    try
                        let path = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-service.log")
                        File.AppendAllText(path, line)
                    with :? IOException as ex ->
                        eprintfn "[fsharp-hotreload][service] Failed to write trace log: %s" ex.Message
                match delta.UpdatedBaseline with
                | Some updatedBaseline ->
                    if trace then
                        printfn
                            "[fsharp-hotreload][service] staging pending baseline encId=%O baseId=%O newBaselineEncId=%O"
                            delta.GenerationId
                            delta.BaseGenerationId
                            updatedBaseline.EncId
                    lock stateLock (fun () ->
                        FSharp.Compiler.HotReloadState.updateBaseline updatedBaseline)
                | None -> ()
                Ok { Delta = delta }
            with
            | HotReloadUnsupportedEditException message ->
                Error(HotReloadError.UnsupportedEdit message)
            | ex ->
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

    member this.EmitDeltaForCompilation(
        tcGlobals: TcGlobals,
        updatedImplementation: CheckedAssemblyAfterOptimization,
        ilModule: ILModuleDef
    ) : Result<DeltaEmissionResult, HotReloadError> =
        // Atomic check-then-restore to prevent TOCTOU race between tryGetSession
        // returning ValueNone and setBaseline being called by another thread.
        //
        // State restoration rationale: In some IDE scenarios, the HotReloadState.session
        // may be cleared by EndSession() while a compilation is still in progress. The
        // lastBaselineState serves as a backup to restore the session state, allowing
        // delta emission to proceed even if the primary state was reset. This ensures
        // continuous delta emission during rapid recompilation cycles without requiring
        // explicit session restart by the host.
        let sessionOpt =
            lock stateLock (fun () ->
                match FSharp.Compiler.HotReloadState.tryGetSession() with
                | ValueNone ->
                    // Restore from backup if primary session was cleared
                    match lastBaselineState with
                    | Some(baseline, implementationFiles) ->
                        FSharp.Compiler.HotReloadState.setBaseline baseline implementationFiles
                        FSharp.Compiler.HotReloadState.tryGetSession()
                    | None -> ValueNone
                | ValueSome _ as session -> session)

        match sessionOpt with
        | ValueNone -> Error HotReloadError.NoActiveSession
        | ValueSome session ->
            use _ =
                Activity.start "HotReload.EmitDeltaForCompilation" [|
                    Activity.Tags.generation, string session.CurrentGeneration
                    Activity.Tags.project, session.Baseline.ModuleId.ToString()
                |]
            let symbolChanges = computeSymbolChanges tcGlobals session.ImplementationFiles updatedImplementation

            if not (List.isEmpty symbolChanges.RudeEdits) then
                Error(HotReloadError.UnsupportedEdit "Rude edits detected; full rebuild required.")
            elif not (List.isEmpty symbolChanges.Deleted) then
                Error(HotReloadError.UnsupportedEdit "Deleted symbols detected; full rebuild required.")
            else
                let updatedTypes, updatedMethods, accessorUpdates = mapSymbolChangesToDelta session.Baseline symbolChanges

                // Insert-only edits (for example, adding an allowed non-virtual method) may not produce
                // method-body updates, but still need to flow to IlxDeltaEmitter so new MethodDef rows are emitted.
                let hasUpdates =
                    not (List.isEmpty updatedTypes)
                    || not (List.isEmpty updatedMethods)
                    || not (List.isEmpty accessorUpdates)
                    || not (List.isEmpty symbolChanges.Added)

                if not hasUpdates then
                    Error HotReloadError.NoChanges
                else
                    let request : DeltaEmissionRequest =
                        { IlModule = ilModule
                          UpdatedTypes = updatedTypes
                          UpdatedMethods = updatedMethods
                          UpdatedAccessors = accessorUpdates
                          SymbolChanges = Some symbolChanges }

                    match this.EmitDelta request with
                    | Ok result ->
                        if result.Delta.UpdatedBaseline.IsSome then
                            this.CommitPendingUpdate(result.Delta.GenerationId)

                        FSharp.Compiler.HotReloadState.updateImplementationFiles updatedImplementation
                        lock stateLock (fun () ->
                            match FSharp.Compiler.HotReloadState.tryGetSession() with
                            | ValueSome updatedSession ->
                                lastBaselineState <- Some(updatedSession.Baseline, updatedImplementation)
                            | ValueNone -> ())
                        Ok result
                    | Error error -> Error error

    /// <summary>Explicit commit hook mirroring Roslyn's service contract.</summary>
    member this.CommitPendingUpdate(generationId: Guid) =
        this.OnDeltaApplied(generationId)

    /// <summary>Explicit discard hook mirroring Roslyn's pending-update semantics.</summary>
    member _.DiscardPendingUpdate() =
        FSharp.Compiler.HotReloadState.discardPendingUpdate()
