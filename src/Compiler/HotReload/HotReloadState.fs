module internal FSharp.Compiler.HotReloadState

open System
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TypedTree

type HotReloadSession =
    {
        Baseline: FSharpEmitBaseline
        ImplementationFiles: CheckedAssemblyAfterOptimization
        CurrentGeneration: int
        PreviousGenerationId: Guid option
        PendingUpdate: PendingHotReloadUpdate option
    }

and PendingHotReloadUpdate =
    {
        GenerationId: Guid
        Baseline: FSharpEmitBaseline
    }

// Session state is intentionally process-scoped today. The checker/service APIs expose
// this as a single active session per process, and starting a new session replaces the old one.
let private sessionLock = obj ()
let mutable private session: HotReloadSession voption = ValueNone
let mutable private lastCommittedSession: HotReloadSession voption = ValueNone

let private toCommittedSnapshot (value: HotReloadSession) =
    { value with
        PendingUpdate = None }

let setBaseline (value: FSharpEmitBaseline) (implementationFiles: CheckedAssemblyAfterOptimization) =
    lock sessionLock (fun () ->
        let previousGenerationId =
            if value.EncId = Guid.Empty then
                None
            else
                Some value.EncId

        let newSession =
            {
                Baseline = value
                ImplementationFiles = implementationFiles
                CurrentGeneration = max 1 value.NextGeneration
                PreviousGenerationId = previousGenerationId
                PendingUpdate = None
            }

        session <-
            ValueSome
                newSession

        lastCommittedSession <- ValueSome(toCommittedSnapshot newSession))

let clearBaseline () =
    lock sessionLock (fun () -> session <- ValueNone)

let clearSessionState () =
    lock sessionLock (fun () ->
        session <- ValueNone
        lastCommittedSession <- ValueNone)

let tryGetBaseline () =
    lock sessionLock (fun () ->
        match session with
        | ValueSome s -> ValueSome s.Baseline
        | ValueNone -> ValueNone)

let tryGetSession () =
    lock sessionLock (fun () -> session)

let tryRestoreSession () =
    lock sessionLock (fun () ->
        match session with
        | ValueSome current -> ValueSome current
        | ValueNone ->
            match lastCommittedSession with
            | ValueSome committed ->
                let restored = toCommittedSnapshot committed
                session <- ValueSome restored
                ValueSome restored
            | ValueNone -> ValueNone)

let updateImplementationFiles (implementationFiles: CheckedAssemblyAfterOptimization) =
    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            let updated =
                {
                    state with
                        ImplementationFiles = implementationFiles
                }

            session <-
                ValueSome
                    updated
            lastCommittedSession <- ValueSome(toCommittedSnapshot updated)
        | ValueNone -> ())

let updateBaseline (baseline: FSharpEmitBaseline) =
    if baseline.EncId = Guid.Empty then
        invalidArg (nameof baseline) "Pending baseline must carry a non-empty EncId."

    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            session <-
                ValueSome
                    {
                        state with
                            PendingUpdate =
                                Some
                                    {
                                        GenerationId = baseline.EncId
                                        Baseline = baseline
                                    }
                    }
        | ValueNone -> ())

let recordDeltaApplied (generationId: Guid) =
    if generationId = Guid.Empty then
        invalidArg (nameof generationId) "Generation ID cannot be empty GUID."

    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            let pending =
                match state.PendingUpdate with
                | Some pending when pending.GenerationId = generationId -> pending
                | Some _ ->
                    invalidArg
                        (nameof generationId)
                        "Generation ID does not match the currently pending hot reload update."
                | None -> invalidOp "Cannot commit delta: no pending hot reload update."

            let updated =
                {
                    state with
                        Baseline = pending.Baseline
                        CurrentGeneration = state.CurrentGeneration + 1
                        PreviousGenerationId = Some generationId
                        PendingUpdate = None
                }

            session <- ValueSome updated
            lastCommittedSession <- ValueSome(toCommittedSnapshot updated)
        | ValueNone ->
            invalidOp "Cannot record delta applied: no active hot reload session.")

let discardPendingUpdate () =
    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            session <-
                ValueSome
                    {
                        state with
                            PendingUpdate = None
                    }
        | ValueNone -> ())
