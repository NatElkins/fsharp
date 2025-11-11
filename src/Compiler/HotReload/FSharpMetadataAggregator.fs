namespace FSharp.Compiler.HotReload

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Linq
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// <summary>
/// Lightweight wrapper around <see cref="MetadataAggregator"/> that retains the baseline reader and the
/// sequence of generation readers. The wrapper mirrors Roslyn’s infrastructure so future metadata-diff logic
/// can plug in without wide churn.
/// </summary>
[<Sealed>]
type FSharpMetadataAggregator(readers: ImmutableArray<MetadataReader>) =
    do
        if readers.IsDefaultOrEmpty then
            invalidArg (nameof readers) "At least one metadata reader is required."

    let readersArray = readers.ToArray()
    let baseline = readersArray.[0]
    let deltas = readersArray |> Array.skip 1
    let metadataAggregator =
        if deltas.Length = 0 then
            None
        else
            Some(MetadataAggregator(baseline, deltas :> IReadOnlyList<MetadataReader>))

    member _.Baseline = baseline
    member _.Deltas = deltas :> seq<MetadataReader>
    member _.Readers = readers

    member _.TranslateHandle(handle: Handle) =
        match metadataAggregator with
        | Some aggregator ->
            let mutable generation = 0
            let translated = aggregator.GetGenerationHandle(handle, &generation)
            struct (generation, translated)
        | None ->
            struct (0, handle)

    member this.TranslateMethodDefinitionHandle(handle: MethodDefinitionHandle) =
        let struct (generation, translated) = this.TranslateHandle(MethodDefinitionHandle.op_Implicit handle)
        struct (generation, MethodDefinitionHandle.op_Explicit translated)

    member this.TranslateStringHandle(handle: StringHandle) =
        let struct (generation, translated) = this.TranslateHandle(StringHandle.op_Implicit handle)
        struct (generation, StringHandle.op_Explicit translated)

    static member Create(readers: seq<MetadataReader>) =
        FSharpMetadataAggregator(ImmutableArray.CreateRange(readers))
