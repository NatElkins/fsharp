module internal FSharp.Compiler.HotReloadState

open System
open FSharp.Compiler.HotReloadBaseline

type HotReloadSession =
    {
        Baseline: FSharpEmitBaseline
        CurrentGeneration: int
        PreviousGenerationId: Guid option
    }

let mutable private session: HotReloadSession voption = ValueNone

let setBaseline (value: FSharpEmitBaseline) =
    session <-
        ValueSome
            {
                Baseline = value
                CurrentGeneration = 1
                PreviousGenerationId = None
            }

let clearBaseline () = session <- ValueNone

let tryGetBaseline () =
    match session with
    | ValueSome s -> ValueSome s.Baseline
    | ValueNone -> ValueNone

let tryGetSession () = session

let recordDeltaApplied (generationId: Guid) =
    match session with
    | ValueSome state ->
        session <-
            ValueSome
                {
                    state with
                        CurrentGeneration = state.CurrentGeneration + 1
                        PreviousGenerationId = Some generationId
                }
    | ValueNone -> ()
