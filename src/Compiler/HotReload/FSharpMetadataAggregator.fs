namespace FSharp.Compiler.HotReload

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Linq
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Microsoft.FSharp.Collections

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
    let readerGeneration = Dictionary<MetadataReader, int>(HashIdentity.Reference)
    do
        readersArray |> Array.iteri (fun generation reader -> readerGeneration[reader] <- generation)
    let tryGetStringValue (reader: MetadataReader) (handle: StringHandle) =
        if handle.IsNil then
            None
        else
            try
                Some(reader.GetString handle)
            with
            | :? BadImageFormatException
            | :? ArgumentOutOfRangeException ->
                None

    let tryGetBlobBytes (reader: MetadataReader) (handle: BlobHandle) =
        if handle.IsNil then
            None
        else
            try
                Some(reader.GetBlobBytes handle)
            with
            | :? BadImageFormatException
            | :? ArgumentOutOfRangeException ->
                None

    let byteArrayComparer : IEqualityComparer<byte[]> =
        { new IEqualityComparer<byte[]> with
            member _.Equals(left, right) =
                if obj.ReferenceEquals(left, right) then
                    true
                elif isNull (box left) || isNull (box right) then
                    false
                elif left.Length <> right.Length then
                    false
                else
                    let mutable idx = 0
                    let mutable equal = true
                    while equal && idx < left.Length do
                        if left[idx] <> right[idx] then
                            equal <- false
                        idx <- idx + 1
                    equal

            member _.GetHashCode(value: byte[]) =
                if isNull (box value) then
                    0
                else
                    let mutable hash = 17
                    for b in value do
                        hash <- (hash * 23) + int b
                    hash }
    let baselineStringHandles =
        let dict = Dictionary<string, StringHandle>(StringComparer.Ordinal)

        let inline addHandle (nameHandle: StringHandle) (reader: MetadataReader) =
            if not nameHandle.IsNil then
                let value = reader.GetString(nameHandle)
                if not (dict.ContainsKey value) then
                    dict[value] <- nameHandle

        let inline collect (handles: seq<'h>) (getName: MetadataReader -> 'h -> StringHandle) (reader: MetadataReader) =
            for handle in handles do
                addHandle (getName reader handle) reader

        let moduleDef = baseline.GetModuleDefinition()
        addHandle moduleDef.Name baseline
        collect baseline.TypeDefinitions (fun r h -> r.GetTypeDefinition(h).Name) baseline
        collect baseline.MethodDefinitions (fun r h -> r.GetMethodDefinition(h).Name) baseline
        collect baseline.PropertyDefinitions (fun r h -> r.GetPropertyDefinition(h).Name) baseline
        collect baseline.EventDefinitions (fun r h -> r.GetEventDefinition(h).Name) baseline
        for methodHandle in baseline.MethodDefinitions do
            let methodDef = baseline.GetMethodDefinition methodHandle
            for parameterHandle in methodDef.GetParameters() do
                addHandle (baseline.GetParameter(parameterHandle).Name) baseline
        dict

    let baselineBlobHandles =
        let dict = Dictionary<byte[], BlobHandle>(byteArrayComparer)

        let addHandle (handle: BlobHandle) (reader: MetadataReader) =
            if not handle.IsNil then
                let bytes = reader.GetBlobBytes(handle)
                if not (dict.ContainsKey bytes) then
                    dict[bytes] <- handle

        for methodHandle in baseline.MethodDefinitions do
            let methodDef = baseline.GetMethodDefinition methodHandle
            addHandle methodDef.Signature baseline

        for propertyHandle in baseline.PropertyDefinitions do
            let propertyDef = baseline.GetPropertyDefinition propertyHandle
            addHandle propertyDef.Signature baseline

        dict
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

    member this.TranslateParameterHandle(handle: ParameterHandle) =
        let struct (generation, translated) = this.TranslateHandle(ParameterHandle.op_Implicit handle)
        struct (generation, ParameterHandle.op_Explicit translated)

    member this.TranslatePropertyHandle(handle: PropertyDefinitionHandle) =
        let struct (generation, translated) = this.TranslateHandle(PropertyDefinitionHandle.op_Implicit handle)
        struct (generation, PropertyDefinitionHandle.op_Explicit translated)

    member this.TranslateEventHandle(handle: EventDefinitionHandle) =
        let struct (generation, translated) = this.TranslateHandle(EventDefinitionHandle.op_Implicit handle)
        struct (generation, EventDefinitionHandle.op_Explicit translated)

    member _.TranslateStringHandle(sourceReader: MetadataReader, handle: StringHandle) =
        let generation =
            match readerGeneration.TryGetValue sourceReader with
            | true, value -> value
            | _ -> invalidArg (nameof sourceReader) "Metadata reader is not part of this aggregator."

        if generation = 0 || handle.IsNil then
            struct (0, handle)
        else
            let offset = MetadataTokens.GetHeapOffset handle
            let heapSize = sourceReader.GetHeapSize(HeapIndex.String)

            if offset >= heapSize then
                // The handle already points into the baseline heap; treat it as generation 0.
                struct (0, handle)
            else
                match tryGetStringValue sourceReader handle with
                | None -> struct (generation, handle)
                | Some value ->
                    match baselineStringHandles.TryGetValue value with
                    | true, baselineHandle -> struct (0, baselineHandle)
                    | _ -> struct (generation, handle)

    member _.TranslateBlobHandle(sourceReader: MetadataReader, handle: BlobHandle) =
        let generation =
            match readerGeneration.TryGetValue sourceReader with
            | true, value -> value
            | _ -> invalidArg (nameof sourceReader) "Metadata reader is not part of this aggregator."

        if generation = 0 || handle.IsNil then
            struct (0, handle)
        else
            let offset = MetadataTokens.GetHeapOffset handle
            let heapSize = sourceReader.GetHeapSize(HeapIndex.Blob)

            if offset >= heapSize then
                struct (0, handle)
            else
                match tryGetBlobBytes sourceReader handle with
                | None -> struct (generation, handle)
                | Some bytes ->
                    match baselineBlobHandles.TryGetValue bytes with
                    | true, baselineHandle -> struct (0, baselineHandle)
                    | _ -> struct (generation, handle)

    static member Create(readers: seq<MetadataReader>) =
        FSharpMetadataAggregator(ImmutableArray.CreateRange(readers))
