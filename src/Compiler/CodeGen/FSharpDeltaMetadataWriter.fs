module internal FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILDeltaHandles
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaMetadataTypes
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.CodeGen.DeltaMetadataSerializer

let private shouldTraceMetadata () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METADATA") with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

let private shouldTraceHeaps () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_HEAPS") with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

let private shouldTraceMethodRows () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METHODS") with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

type MethodDefinitionRowInfo = DeltaMetadataTypes.MethodDefinitionRowInfo

type ParameterDefinitionRowInfo = DeltaMetadataTypes.ParameterDefinitionRowInfo

type MethodMetadataUpdate =
    {
        MethodKey: MethodDefinitionKey
        MethodToken: int
        MethodHandle: MethodDefinitionHandle
        Body: MethodBodyUpdate
    }

type PropertyDefinitionRowInfo = DeltaMetadataTypes.PropertyDefinitionRowInfo

type EventDefinitionRowInfo = DeltaMetadataTypes.EventDefinitionRowInfo

type PropertyMapRowInfo = DeltaMetadataTypes.PropertyMapRowInfo

type EventMapRowInfo = DeltaMetadataTypes.EventMapRowInfo

type MethodSemanticsMetadataUpdate = DeltaMetadataTypes.MethodSemanticsMetadataUpdate
type StandaloneSignatureUpdate = FSharp.Compiler.IlxDeltaStreams.StandaloneSignatureUpdate

type MetadataDelta =
    {
        Metadata: byte[]
        StringHeap: byte[]
        BlobHeap: byte[]
        GuidHeap: byte[]
        EncLog: (TableIndex * int * EditAndContinueOperation) array
        EncMap: (TableIndex * int) array
        TableRowCounts: int[]
        HeapSizes: MetadataHeapSizes
        HeapOffsets: MetadataHeapOffsets
        Tables: TableRows
        TableBitMasks: TableBitMasks
        IndexSizes: DeltaIndexSizing.CodedIndexSizes
        TableStream: DeltaTableStream
        /// The EncId GUID for this generation (used as EncBaseId for subsequent generations)
        GenerationId: Guid
        /// The EncBaseId GUID (EncId of the previous generation, or Empty for generation 1)
        BaseGenerationId: Guid
    }

let emitWithUserStrings
    (moduleName: string)
    (moduleNameOffset: StringOffset option)
    (generation: int)
    (encId: Guid)
    (encBaseId: Guid)
    (moduleId: Guid)
    (methodDefinitionRows: MethodDefinitionRowInfo list)
    (parameterDefinitionRows: ParameterDefinitionRowInfo list)
    (typeReferenceRows: TypeReferenceRowInfo list)
    (memberReferenceRows: MemberReferenceRowInfo list)
    (assemblyReferenceRows: AssemblyReferenceRowInfo list)
    (propertyDefinitionRows: PropertyDefinitionRowInfo list)
    (eventDefinitionRows: EventDefinitionRowInfo list)
    (propertyMapRows: PropertyMapRowInfo list)
    (eventMapRows: EventMapRowInfo list)
    (methodSemanticsRows: MethodSemanticsMetadataUpdate list)
    (standaloneSignatureRows: StandaloneSignatureUpdate list)
    (customAttributeRows: CustomAttributeRowInfo list)
    (userStringUpdates: (int * int * string) list)
    (updates: MethodMetadataUpdate list)
    (heapOffsets: MetadataHeapOffsets)
    (externalRowCounts: int[])
    : MetadataDelta =
    if shouldTraceMetadata () then
        printfn "[fsharp-hotreload][metadata-writer] emit invoked updates=%d" (List.length updates)
        for row in methodDefinitionRows do
            let offset =
                match row.NameOffset with
                | Some (StringOffset o) -> Some o
                | None -> None
            printfn
                "[fsharp-hotreload][metadata-writer] method-row name=%s isAdded=%b offset=%A"
                row.Name
                row.IsAdded
                offset
    let normalizedExternalRowCounts =
        if externalRowCounts.Length = MetadataTokens.TableCount then
            externalRowCounts
        else
            Array.zeroCreate MetadataTokens.TableCount

    if List.isEmpty updates then
        let emptyMirror = DeltaMetadataTables(heapOffsets)
        let emptySizes = DeltaMetadataSerializer.computeMetadataSizes emptyMirror normalizedExternalRowCounts

        { Metadata = Array.empty
          StringHeap = Array.empty
          BlobHeap = Array.empty
          GuidHeap = Array.empty
          EncLog = Array.empty
          EncMap = Array.empty
          TableRowCounts = emptySizes.RowCounts
          HeapSizes = emptySizes.HeapSizes
          HeapOffsets = heapOffsets
          Tables = emptyMirror.TableRows
          TableBitMasks = emptySizes.BitMasks
          IndexSizes = emptySizes.IndexSizes
          TableStream =
            { Bytes = Array.empty
              UnpaddedSize = 0
              PaddedSize = 0 }
          GenerationId = encId
          BaseGenerationId = encBaseId }
    else

        printfn "[emitWithUserStrings] generation=%d moduleId=%A encId=%A encBaseId=%A" generation moduleId encId encBaseId
        let tableMirror = DeltaMetadataTables(heapOffsets)
        tableMirror.AddModuleRow(moduleName, moduleNameOffset, generation, moduleId, encId, encBaseId)

        let updatesByKey = Dictionary<MethodDefinitionKey, MethodMetadataUpdate>(HashIdentity.Structural)
        for update in updates do
            updatesByKey[update.MethodKey] <- update

        let mutable encLog = ResizeArray()
        let mutable encMap = ResizeArray()

        encLog.Add(struct (TableIndex.Module, 1, EditAndContinueOperation.Default))
        encMap.Add(struct (TableIndex.Module, 1))

        for row in methodDefinitionRows do
            match updatesByKey.TryGetValue row.Key with
            | true, update ->
                tableMirror.AddMethodRow(row, update.Body)
                if shouldTraceMethodRows () then
                    printfn
                        "[fsharp-hotreload][writer] method-row key=%s::%s rowId=%d isAdded=%b"
                        row.Key.DeclaringType
                        row.Key.Name
                        row.RowId
                        row.IsAdded

                let operation = if row.IsAdded then EditAndContinueOperation.AddMethod else EditAndContinueOperation.Default
                encLog.Add(struct (TableIndex.MethodDef, row.RowId, operation))
                encMap.Add(struct (TableIndex.MethodDef, row.RowId))
            | _ ->
                if shouldTraceMetadata () then
                    printfn "[fsharp-hotreload][metadata-writer] missing update payload for %A" row.Key

        for row in parameterDefinitionRows do
            tableMirror.AddParameterRow row

            let operation = if row.IsAdded then EditAndContinueOperation.AddParameter else EditAndContinueOperation.Default
            encLog.Add(struct (TableIndex.Param, row.RowId, operation))
            encMap.Add(struct (TableIndex.Param, row.RowId))

        for row in typeReferenceRows do
            tableMirror.AddTypeReferenceRow row

            encLog.Add(struct (TableIndex.TypeRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.TypeRef, row.RowId))

        for row in memberReferenceRows do
            tableMirror.AddMemberReferenceRow row

            encLog.Add(struct (TableIndex.MemberRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.MemberRef, row.RowId))

        for row in assemblyReferenceRows do
            tableMirror.AddAssemblyReferenceRow row

            encLog.Add(struct (TableIndex.AssemblyRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.AssemblyRef, row.RowId))

        for signature in standaloneSignatureRows do
            let rowId = MetadataTokens.GetRowNumber signature.Handle
            tableMirror.AddStandaloneSignatureRow(signature.Blob)

            let operation = EditAndContinueOperation.Default
            encLog.Add(struct (TableIndex.StandAloneSig, rowId, operation))
            encMap.Add(struct (TableIndex.StandAloneSig, rowId))

        for row in customAttributeRows do
            tableMirror.AddCustomAttributeRow row

            encLog.Add(struct (TableIndex.CustomAttribute, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.CustomAttribute, row.RowId))

        for row in propertyDefinitionRows do
            if row.IsAdded then
                tableMirror.AddPropertyRow row

                encLog.Add(struct (TableIndex.Property, row.RowId, EditAndContinueOperation.AddProperty))
                encMap.Add(struct (TableIndex.Property, row.RowId))

        for row in eventDefinitionRows do
            if row.IsAdded then
                tableMirror.AddEventRow row

                encLog.Add(struct (TableIndex.Event, row.RowId, EditAndContinueOperation.AddEvent))
                encMap.Add(struct (TableIndex.Event, row.RowId))

        for row in propertyMapRows do
            if row.IsAdded then
                encLog.Add(struct (TableIndex.PropertyMap, row.RowId, EditAndContinueOperation.AddProperty))
                encMap.Add(struct (TableIndex.PropertyMap, row.RowId))
                tableMirror.AddPropertyMapRow row

        for row in eventMapRows do
            if row.IsAdded then
                encLog.Add(struct (TableIndex.EventMap, row.RowId, EditAndContinueOperation.AddEvent))
                encMap.Add(struct (TableIndex.EventMap, row.RowId))
                tableMirror.AddEventMapRow row

        for row in methodSemanticsRows do
            if row.IsAdded then
                tableMirror.AddMethodSemanticsRow row

                encLog.Add(struct (TableIndex.MethodSemantics, row.RowId, EditAndContinueOperation.AddMethod))
                encMap.Add(struct (TableIndex.MethodSemantics, row.RowId))

        for _, newToken, literal in userStringUpdates do
            let offset = newToken &&& 0x00FFFFFF
            tableMirror.AddUserStringLiteral(offset, literal)

        let encLogEntries =
            let snapshot = encLog |> Seq.toArray
            let orderedTables =
                [| TableIndex.Module
                   TableIndex.MethodDef
                   TableIndex.Param
                   TableIndex.TypeRef
                   TableIndex.MemberRef
                   TableIndex.AssemblyRef
                   TableIndex.StandAloneSig
                   TableIndex.CustomAttribute
                   TableIndex.Property
                   TableIndex.Event
                   TableIndex.PropertyMap
                   TableIndex.EventMap
                   TableIndex.MethodSemantics |]

            let orderedTableSet = orderedTables |> Set.ofArray
            let builder = ResizeArray()

            let appendEntries tableIndex =
                snapshot
                |> Array.filter (fun struct (table, _, _) -> table = tableIndex)
                |> Array.sortBy (fun struct (_, rowId, _) -> rowId)
                |> Array.iter builder.Add

            orderedTables |> Array.iter appendEntries

            snapshot
            |> Array.filter (fun struct (table, _, _) -> not (orderedTableSet.Contains table))
            |> Array.sortBy (fun struct (tableIndex, rowId, _) ->
                ((int tableIndex) <<< 24) ||| (rowId &&& 0x00FFFFFF))
            |> Array.iter builder.Add

            builder.ToArray()

        let encMapEntries =
            encMap
            |> Seq.sortBy (fun struct (tableIndex, rowId) ->
                ((int tableIndex) <<< 24) ||| (rowId &&& 0x00FFFFFF))
            |> Seq.toArray

        for struct (tableIndex, rowId, operation) in encLogEntries do
            tableMirror.AddEncLogRow(tableIndex, rowId, operation)

        for struct (tableIndex, rowId) in encMapEntries do
            tableMirror.AddEncMapRow(tableIndex, rowId)

        let metadataSizes = DeltaMetadataSerializer.computeMetadataSizes tableMirror normalizedExternalRowCounts
        let tableRowCounts = metadataSizes.RowCounts
        let tableBitMasks = metadataSizes.BitMasks
        let indexSizes = metadataSizes.IndexSizes

        let tableStreamInput =
            { DeltaMetadataSerializer.DeltaTableSerializerInput.Tables = tableMirror.TableRows
              MetadataSizes = metadataSizes
              StringHeap = tableMirror.StringHeapBytes
              StringHeapOffsets = tableMirror.StringHeapOffsets
              BlobHeap = tableMirror.BlobHeapBytes
              BlobHeapOffsets = tableMirror.BlobHeapOffsets
              GuidHeap = tableMirror.GuidHeapBytes
              HeapOffsets = heapOffsets }

        let tableStream = DeltaMetadataSerializer.buildTableStream tableStreamInput
        let heapStreams = DeltaMetadataSerializer.buildHeapStreams tableMirror
        let metadataBytes = DeltaMetadataSerializer.serializeMetadataRoot tableStreamInput heapStreams tableStream

        if shouldTraceMetadata () then
            printfn
                "[fsharp-hotreload][index-sizes] stringsBig=%b guidsBig=%b blobsBig=%b"
                indexSizes.StringsBig
                indexSizes.GuidsBig
                indexSizes.BlobsBig
            let methodRows = tableRowCounts[int TableIndex.MethodDef]
            let paramRows = tableRowCounts[int TableIndex.Param]
            let propertyRows = tableRowCounts[int TableIndex.Property]
            let eventRows = tableRowCounts[int TableIndex.Event]
            printfn
                "[fsharp-hotreload][metadata-writer] rows method=%d param=%d property=%d event=%d stringHeap=%d blobHeap=%d guidHeap=%d"
                methodRows
                paramRows
                propertyRows
                eventRows
                heapStreams.StringsLength
                heapStreams.BlobsLength
                heapStreams.GuidsLength

        if shouldTraceHeaps () then
            printfn
                "[fsharp-hotreload][heap-summary] baseline:string=%d blob=%d guid=%d | delta:string=%d blob=%d guid=%d"
                heapOffsets.StringHeapStart
                heapOffsets.BlobHeapStart
                heapOffsets.GuidHeapStart
                heapStreams.StringsLength
                heapStreams.BlobsLength
                heapStreams.GuidsLength
            printfn "[fsharp-hotreload][heap-bytes] blob-bytes=%A" heapStreams.Blobs

        // Debug: verify module GenerationId/BaseGenerationId encoding
        try
            use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(metadataBytes))
            let reader = provider.GetMetadataReader()
            let moduleDef = reader.GetModuleDefinition()
            let genIdIndex =
                if moduleDef.GenerationId.IsNil then 0 else (MetadataTokens.GetHeapOffset moduleDef.GenerationId / 16) + 1
            let baseGenIdIndex =
                if moduleDef.BaseGenerationId.IsNil then 0 else (MetadataTokens.GetHeapOffset moduleDef.BaseGenerationId / 16) + 1
            let guidHeapSize = reader.GetHeapSize(HeapIndex.Guid)
            printfn
                "[fsharp-hotreload][module-row-debug] generation=%d genIdIndex=%d baseGenIdIndex=%d guidHeapSize=%d"
                generation
                genIdIndex
                baseGenIdIndex
                guidHeapSize
        with _ -> ()

        { Metadata = metadataBytes
          StringHeap = heapStreams.Strings
          BlobHeap = heapStreams.Blobs
          GuidHeap = heapStreams.Guids
          EncLog = encLogEntries |> Array.map (fun struct (a, b, c) -> (a, b, c))
          EncMap = encMapEntries |> Array.map (fun struct (a, b) -> (a, b))
          TableRowCounts = tableRowCounts
          HeapSizes = metadataSizes.HeapSizes
          HeapOffsets = heapOffsets
          Tables = tableMirror.TableRows
          TableBitMasks = tableBitMasks
          IndexSizes = indexSizes
          TableStream = tableStream
          GenerationId = encId
          BaseGenerationId = encBaseId }

let emitWithReferences
    (moduleName: string)
    (moduleNameOffset: StringOffset option)
    (generation: int)
    (encId: Guid)
    (encBaseId: Guid)
    (moduleId: Guid)
    (methodDefinitionRows: MethodDefinitionRowInfo list)
    (parameterDefinitionRows: ParameterDefinitionRowInfo list)
    (typeReferenceRows: TypeReferenceRowInfo list)
    (memberReferenceRows: MemberReferenceRowInfo list)
    (assemblyReferenceRows: AssemblyReferenceRowInfo list)
    (propertyDefinitionRows: PropertyDefinitionRowInfo list)
    (eventDefinitionRows: EventDefinitionRowInfo list)
    (propertyMapRows: PropertyMapRowInfo list)
    (eventMapRows: EventMapRowInfo list)
    (methodSemanticsRows: MethodSemanticsMetadataUpdate list)
    (standaloneSignatureRows: StandaloneSignatureUpdate list)
    (customAttributeRows: CustomAttributeRowInfo list)
    (userStringUpdates: (int * int * string) list)
    (updates: MethodMetadataUpdate list)
    (heapOffsets: MetadataHeapOffsets)
    (externalRowCounts: int[])
    : MetadataDelta =
    emitWithUserStrings
        moduleName
        moduleNameOffset
        generation
        encId
        encBaseId
        moduleId
        methodDefinitionRows
        parameterDefinitionRows
        typeReferenceRows
        memberReferenceRows
        assemblyReferenceRows
        propertyDefinitionRows
        eventDefinitionRows
        propertyMapRows
        eventMapRows
        methodSemanticsRows
        standaloneSignatureRows
        customAttributeRows
        userStringUpdates
        updates
        heapOffsets
        externalRowCounts

let emit
    (moduleName: string)
    (moduleNameOffset: StringOffset option)
    (generation: int)
    (encId: Guid)
    (encBaseId: Guid)
    (moduleId: Guid)
    (methodDefinitionRows: MethodDefinitionRowInfo list)
    (parameterDefinitionRows: ParameterDefinitionRowInfo list)
    (propertyDefinitionRows: PropertyDefinitionRowInfo list)
    (eventDefinitionRows: EventDefinitionRowInfo list)
    (propertyMapRows: PropertyMapRowInfo list)
    (eventMapRows: EventMapRowInfo list)
    (methodSemanticsRows: MethodSemanticsMetadataUpdate list)
    (standaloneSignatureRows: StandaloneSignatureUpdate list)
    (customAttributeRows: CustomAttributeRowInfo list)
    (updates: MethodMetadataUpdate list)
    (heapOffsets: MetadataHeapOffsets)
    (externalRowCounts: int[])
    : MetadataDelta =
    emitWithReferences
        moduleName
        moduleNameOffset
        generation
        encId
        encBaseId
        moduleId
        methodDefinitionRows
        parameterDefinitionRows
        []
        []
        []
        propertyDefinitionRows
        eventDefinitionRows
        propertyMapRows
        eventMapRows
        methodSemanticsRows
        standaloneSignatureRows
        customAttributeRows
        ([] : (int * int * string) list)
        updates
        heapOffsets
        externalRowCounts
