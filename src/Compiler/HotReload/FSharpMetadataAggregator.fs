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
        if readers.IsDefault then
            invalidArg (nameof readers) "Readers array is uninitialized (default struct value)."
        elif readers.IsEmpty then
            invalidArg (nameof readers) "At least one metadata reader is required."

    let readersArray = readers.ToArray()
    let baseline = readersArray.[0]
    let deltas = readersArray |> Array.skip 1
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
                    // FNV-1a hash for better collision resistance
                    // See: http://www.isthe.com/chongo/tech/comp/fnv/
                    let mutable hash = 0x811c9dc5 // FNV offset basis
                    for b in value do
                        hash <- hash ^^^ int b
                        hash <- hash * 0x01000193 // FNV prime
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

        let standaloneHandles =
            let count = baseline.GetTableRowCount TableIndex.StandAloneSig
            seq {
                for row in 1 .. count do
                    yield MetadataTokens.StandaloneSignatureHandle row
            }

        for standaloneHandle in standaloneHandles do
            let standalone = baseline.GetStandaloneSignature standaloneHandle
            addHandle standalone.Signature baseline

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

    member this.TranslateStringHandle(sourceReader: MetadataReader, handle: StringHandle) =
        if handle.IsNil then
            struct (0, handle)
        else
            match metadataAggregator with
            | Some _ ->
                let struct (generation, translatedHandle) =
                    this.TranslateHandle(StringHandle.op_Implicit handle)

                let translatedString = StringHandle.op_Explicit translatedHandle

                if generation = 0 then
                    struct (0, translatedString)
                else
                    match tryGetStringValue sourceReader translatedString with
                    | Some value ->
                        match baselineStringHandles.TryGetValue value with
                        | true, baselineHandle -> struct (0, baselineHandle)
                        | _ -> struct (generation, translatedString)
                    | None ->
                        struct (generation, translatedString)
            | None ->
                struct (0, handle)

    member this.TranslateBlobHandle(sourceReader: MetadataReader, handle: BlobHandle) =
        if handle.IsNil then
            struct (0, handle)
        else
            match metadataAggregator with
            | Some _ ->
                let struct (generation, translatedHandle) =
                    this.TranslateHandle(BlobHandle.op_Implicit handle)

                let translatedBlob = BlobHandle.op_Explicit translatedHandle

                if generation = 0 then
                    struct (0, translatedBlob)
                else
                    match tryGetBlobBytes sourceReader translatedBlob with
                    | Some bytes ->
                        match baselineBlobHandles.TryGetValue bytes with
                        | true, baselineHandle -> struct (0, baselineHandle)
                        | _ -> struct (generation, translatedBlob)
                    | None ->
                        struct (generation, translatedBlob)
            | None ->
                struct (0, handle)

    static member Create(readers: seq<MetadataReader>) =
        FSharpMetadataAggregator(ImmutableArray.CreateRange(readers))
