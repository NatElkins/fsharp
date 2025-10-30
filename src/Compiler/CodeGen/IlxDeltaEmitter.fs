module internal FSharp.Compiler.IlxDeltaEmitter

open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.HotReloadBaseline

/// Represents the emitted artifacts for a hot reload delta.
type IlxDelta =
    {
        Metadata: byte[]
        IL: byte[]
        Pdb: byte[] option
        EncLog: (TableIndex * int * EditAndContinueOperation) array
        EncMap: (TableIndex * int) array
        UpdatedTypeTokens: int list
        UpdatedMethodTokens: int list
    }

/// Request payload used when producing a delta. This will accumulate more fields as the emitter is implemented.
type IlxDeltaRequest = { Baseline: FSharpEmitBaseline }

/// Helper that produces an empty delta payload.
let private emptyDelta: IlxDelta =
    {
        Metadata = Array.empty
        IL = Array.empty
        Pdb = None
        EncLog = Array.empty
        EncMap = Array.empty
        UpdatedTypeTokens = []
        UpdatedMethodTokens = []
    }

/// Emits the delta artifacts for a request. The current implementation returns an empty payload and acts as a placeholder
/// for the full metadata/IL delta emission pipeline.
let emitDelta (_request: IlxDeltaRequest) : IlxDelta = emptyDelta
