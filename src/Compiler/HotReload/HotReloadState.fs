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
    }

let private sessionLock = obj ()
let mutable private session: HotReloadSession voption = ValueNone

let setBaseline (value: FSharpEmitBaseline) (implementationFiles: CheckedAssemblyAfterOptimization) =
    lock sessionLock (fun () ->
        let previousGenerationId =
            if value.EncId = Guid.Empty then
                None
            else
                Some value.EncId

        session <-
            ValueSome
                {
                    Baseline = value
                    ImplementationFiles = implementationFiles
                    CurrentGeneration = max 1 value.NextGeneration
                    PreviousGenerationId = previousGenerationId
                })

let clearBaseline () =
    lock sessionLock (fun () -> session <- ValueNone)

let tryGetBaseline () =
    lock sessionLock (fun () ->
        match session with
        | ValueSome s -> ValueSome s.Baseline
        | ValueNone -> ValueNone)

let tryGetSession () =
    lock sessionLock (fun () -> session)

let updateImplementationFiles (implementationFiles: CheckedAssemblyAfterOptimization) =
    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            session <-
                ValueSome
                    {
                        state with
                            ImplementationFiles = implementationFiles
                    }
        | ValueNone -> ())

let updateBaseline (baseline: FSharpEmitBaseline) =
    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            session <-
                ValueSome
                    {
                        state with
                            Baseline = baseline
                    }
        | ValueNone -> ())

let recordDeltaApplied (generationId: Guid) =
    lock sessionLock (fun () ->
        match session with
        | ValueSome state ->
            session <-
                ValueSome
                    {
                        state with
                            CurrentGeneration = state.CurrentGeneration + 1
                            PreviousGenerationId = Some generationId
                    }
        | ValueNone -> ())
