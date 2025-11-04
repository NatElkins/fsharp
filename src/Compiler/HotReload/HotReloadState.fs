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

let mutable private session: HotReloadSession voption = ValueNone

let setBaseline (value: FSharpEmitBaseline) (implementationFiles: CheckedAssemblyAfterOptimization) =
    session <-
        ValueSome
            {
                Baseline = value
                ImplementationFiles = implementationFiles
                CurrentGeneration = 1
                PreviousGenerationId = None
            }

let clearBaseline () = session <- ValueNone

let tryGetBaseline () =
    match session with
    | ValueSome s -> ValueSome s.Baseline
    | ValueNone -> ValueNone

let tryGetSession () = session

let updateImplementationFiles (implementationFiles: CheckedAssemblyAfterOptimization) =
    match session with
    | ValueSome state ->
        session <-
            ValueSome
                {
                    state with
                        ImplementationFiles = implementationFiles
                }
    | ValueNone -> ()

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
