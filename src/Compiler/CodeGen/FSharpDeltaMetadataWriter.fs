module internal FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaMetadataTypes
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.CodeGen.DeltaMetadataSerializer

let private serializeWithMetadataBuilder (metadataBuilder: MetadataBuilder) =
    let metadataRoot = MetadataRootBuilder(metadataBuilder)
    let blob = BlobBuilder()
    metadataRoot.Serialize(blob, methodBodyStreamRva = 0, mappedFieldDataStreamRva = 0)
    blob.ToArray()

let private shouldTraceMetadata () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METADATA") with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

let private shouldEmitMetadataBuilderTables () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_USE_SRM_TABLES") with
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
    }

let emitWithUserStrings
    (metadataBuilder: MetadataBuilder)
    (moduleName: string)
    (moduleNameHandle: StringHandle option)
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
                match row.NameHandle with
                | Some handle -> MetadataTokens.GetHeapOffset handle |> Some
                | None -> None
            printfn
                "[fsharp-hotreload][metadata-writer] method-row name=%s isAdded=%b handle=%A"
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
              PaddedSize = 0 } }
    else

        // Ensure tables not emitted in the current delta remain empty to satisfy metadata writer invariants.
        let methodUpdateCount = methodDefinitionRows |> List.length
        let parameterUpdateCount = parameterDefinitionRows |> List.length
        let standaloneSigCount = standaloneSignatureRows |> List.length
        let customAttributeCount = customAttributeRows |> List.length
        let typeRefCount = typeReferenceRows |> List.length
        let memberRefCount = memberReferenceRows |> List.length
        let assemblyRefCount = assemblyReferenceRows |> List.length
        let propertyUpdateCount = propertyDefinitionRows |> List.length
        let eventUpdateCount = eventDefinitionRows |> List.length
        let propertyMapLogCount = propertyMapRows |> List.length
        let propertyMapAddCount = propertyMapRows |> List.filter (fun row -> row.IsAdded) |> List.length
        let eventMapLogCount = eventMapRows |> List.length
        let eventMapAddCount = eventMapRows |> List.filter (fun row -> row.IsAdded) |> List.length
        let methodSemanticsUpdateCount = methodSemanticsRows |> List.length

        let emitSrmTables = shouldEmitMetadataBuilderTables ()

        if emitSrmTables then
            metadataBuilder.SetCapacity(TableIndex.Module, 1)
            metadataBuilder.SetCapacity(TableIndex.TypeRef, typeRefCount)
            metadataBuilder.SetCapacity(TableIndex.TypeDef, 0)
            metadataBuilder.SetCapacity(TableIndex.Field, 0)
            metadataBuilder.SetCapacity(TableIndex.MethodDef, methodUpdateCount)
            metadataBuilder.SetCapacity(TableIndex.Param, parameterUpdateCount)
            metadataBuilder.SetCapacity(TableIndex.InterfaceImpl, 0)
            metadataBuilder.SetCapacity(TableIndex.MemberRef, memberRefCount)
            metadataBuilder.SetCapacity(TableIndex.Constant, 0)
            metadataBuilder.SetCapacity(TableIndex.CustomAttribute, customAttributeCount)
            metadataBuilder.SetCapacity(TableIndex.FieldMarshal, 0)
            metadataBuilder.SetCapacity(TableIndex.DeclSecurity, 0)
            metadataBuilder.SetCapacity(TableIndex.ClassLayout, 0)
            metadataBuilder.SetCapacity(TableIndex.FieldLayout, 0)
            metadataBuilder.SetCapacity(TableIndex.StandAloneSig, standaloneSigCount)
            metadataBuilder.SetCapacity(TableIndex.EventMap, eventMapAddCount)
            metadataBuilder.SetCapacity(TableIndex.Event, eventUpdateCount)
            metadataBuilder.SetCapacity(TableIndex.PropertyMap, propertyMapAddCount)
            metadataBuilder.SetCapacity(TableIndex.Property, propertyUpdateCount)
            metadataBuilder.SetCapacity(TableIndex.MethodSemantics, methodSemanticsUpdateCount)
            metadataBuilder.SetCapacity(TableIndex.MethodImpl, 0)
            metadataBuilder.SetCapacity(TableIndex.ModuleRef, 0)
            metadataBuilder.SetCapacity(TableIndex.TypeSpec, 0)
            metadataBuilder.SetCapacity(TableIndex.ImplMap, 0)
            metadataBuilder.SetCapacity(TableIndex.FieldRva, 0)
        let encEntryCount =
            methodUpdateCount
            + parameterUpdateCount
            + standaloneSigCount
            + typeRefCount
            + memberRefCount
            + assemblyRefCount
            + propertyUpdateCount
            + eventUpdateCount
            + propertyMapLogCount
            + eventMapLogCount
            + methodSemanticsUpdateCount
            + customAttributeCount
        metadataBuilder.SetCapacity(TableIndex.EncLog, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.EncMap, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.Assembly, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyOS, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRef, assemblyRefCount)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefOS, 0)
        metadataBuilder.SetCapacity(TableIndex.File, 0)
        metadataBuilder.SetCapacity(TableIndex.ExportedType, 0)
        metadataBuilder.SetCapacity(TableIndex.ManifestResource, 0)
        metadataBuilder.SetCapacity(TableIndex.NestedClass, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParam, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodSpec, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParamConstraint, 0)

        let moduleNameTokenOpt =
            match moduleNameHandle with
            | Some handle when not handle.IsNil -> Some handle
            | _ -> None
        let moduleNameHandleOrAdded =
            match moduleNameHandle with
            | Some handle when not handle.IsNil -> handle
            | _ -> metadataBuilder.GetOrAddString(moduleName)
        let mvidHandle = metadataBuilder.GetOrAddGuid(moduleId)
        let encIdHandle = metadataBuilder.GetOrAddGuid(encId)
        let encBaseHandle = metadataBuilder.GetOrAddGuid(encBaseId)
        let _ = metadataBuilder.AddModule(0, moduleNameHandleOrAdded, mvidHandle, encIdHandle, encBaseHandle)
        let tableMirror = DeltaMetadataTables(heapOffsets)
        tableMirror.AddModuleRow(moduleName, moduleNameTokenOpt, moduleId, encId, encBaseId)

        let entityHandleFromTable tableIndex rowId =
            MetadataTokens.Handle(tableIndex, rowId)
            |> EntityHandle.op_Explicit

        let resolutionScopeHandle struct (kind, rowId) =
            match kind with
            | HandleKind.ModuleDefinition -> entityHandleFromTable TableIndex.Module rowId
            | HandleKind.ModuleReference -> entityHandleFromTable TableIndex.ModuleRef rowId
            | HandleKind.AssemblyReference -> entityHandleFromTable TableIndex.AssemblyRef rowId
            | HandleKind.TypeReference -> entityHandleFromTable TableIndex.TypeRef rowId
            | _ -> EntityHandle()

        let memberRefParentHandle struct (kind, rowId) =
            match kind with
            | HandleKind.TypeDefinition -> entityHandleFromTable TableIndex.TypeDef rowId
            | HandleKind.TypeReference -> entityHandleFromTable TableIndex.TypeRef rowId
            | HandleKind.ModuleReference -> entityHandleFromTable TableIndex.ModuleRef rowId
            | HandleKind.MethodDefinition -> entityHandleFromTable TableIndex.MethodDef rowId
            | HandleKind.TypeSpecification -> entityHandleFromTable TableIndex.TypeSpec rowId
            | _ -> EntityHandle()

        let updatesByKey = Dictionary<MethodDefinitionKey, MethodMetadataUpdate>(HashIdentity.Structural)
        for update in updates do
            updatesByKey[update.MethodKey] <- update

        let mutable encLog = ResizeArray()
        let mutable encMap = ResizeArray()

        for row in methodDefinitionRows do
            match updatesByKey.TryGetValue row.Key with
            | true, update ->
                if row.IsAdded then
                    if emitSrmTables then
                        let nameHandle = metadataBuilder.GetOrAddString row.Name
                        let signatureHandle = metadataBuilder.GetOrAddBlob row.Signature

                        metadataBuilder.AddMethodDefinition(
                            row.Attributes,
                            row.ImplAttributes,
                            nameHandle,
                            signatureHandle,
                            update.Body.CodeOffset,
                            ParameterHandle()
                        )
                        |> ignore
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
            if emitSrmTables then
                let nameHandle =
                    match row.Name with
                    | Some name -> metadataBuilder.GetOrAddString name
                    | None -> StringHandle()
                metadataBuilder.AddParameter(row.Attributes, nameHandle, row.SequenceNumber) |> ignore
            tableMirror.AddParameterRow row

            let operation = if row.IsAdded then EditAndContinueOperation.AddParameter else EditAndContinueOperation.Default
            encLog.Add(struct (TableIndex.Param, row.RowId, operation))
            encMap.Add(struct (TableIndex.Param, row.RowId))

        for row in typeReferenceRows do
            if emitSrmTables then
                let scopeHandle = resolutionScopeHandle row.ResolutionScope
                let namespaceHandle =
                    if String.IsNullOrEmpty row.Namespace then
                        StringHandle()
                    else
                        metadataBuilder.GetOrAddString row.Namespace
                let nameHandle = metadataBuilder.GetOrAddString row.Name
                metadataBuilder.AddTypeReference(scopeHandle, namespaceHandle, nameHandle) |> ignore
            tableMirror.AddTypeReferenceRow row

            encLog.Add(struct (TableIndex.TypeRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.TypeRef, row.RowId))

        for row in memberReferenceRows do
            if emitSrmTables then
                let parentHandle = memberRefParentHandle row.Parent
                let nameHandle = metadataBuilder.GetOrAddString row.Name
                let signatureHandle =
                    if isNull (box row.Signature) || row.Signature.Length = 0 then
                        BlobHandle()
                    else
                        metadataBuilder.GetOrAddBlob row.Signature
                metadataBuilder.AddMemberReference(parentHandle, nameHandle, signatureHandle) |> ignore
            tableMirror.AddMemberReferenceRow row

            encLog.Add(struct (TableIndex.MemberRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.MemberRef, row.RowId))

        for row in assemblyReferenceRows do
            if emitSrmTables then
                let nameHandle = metadataBuilder.GetOrAddString row.Name
                let cultureHandle =
                    match row.Culture with
                    | Some culture when not (String.IsNullOrEmpty culture) -> metadataBuilder.GetOrAddString culture
                    | _ -> StringHandle()
                let publicKeyHandle =
                    if isNull (box row.PublicKeyOrToken) || row.PublicKeyOrToken.Length = 0 then
                        BlobHandle()
                    else
                        metadataBuilder.GetOrAddBlob row.PublicKeyOrToken
                let hashHandle =
                    if isNull (box row.HashValue) || row.HashValue.Length = 0 then
                        BlobHandle()
                    else
                        metadataBuilder.GetOrAddBlob row.HashValue
                metadataBuilder.AddAssemblyReference(nameHandle, row.Version, cultureHandle, publicKeyHandle, row.Flags, hashHandle)
                |> ignore
            tableMirror.AddAssemblyReferenceRow row

            encLog.Add(struct (TableIndex.AssemblyRef, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.AssemblyRef, row.RowId))

        for signature in standaloneSignatureRows do
            let rowId = MetadataTokens.GetRowNumber signature.Handle
            tableMirror.AddStandaloneSignatureRow(signature.Blob)

            let operation = EditAndContinueOperation.Default
            encLog.Add(struct (TableIndex.StandAloneSig, rowId, operation))
            encMap.Add(struct (TableIndex.StandAloneSig, rowId))

        let entityHandleOf kind rowId =
            match kind with
            | HandleKind.MethodDefinition -> entityHandleFromTable TableIndex.MethodDef rowId
            | HandleKind.PropertyDefinition -> entityHandleFromTable TableIndex.Property rowId
            | HandleKind.EventDefinition -> entityHandleFromTable TableIndex.Event rowId
            | HandleKind.MemberReference -> entityHandleFromTable TableIndex.MemberRef rowId
            | HandleKind.TypeReference -> entityHandleFromTable TableIndex.TypeRef rowId
            | HandleKind.TypeDefinition -> entityHandleFromTable TableIndex.TypeDef rowId
            | HandleKind.TypeSpecification -> entityHandleFromTable TableIndex.TypeSpec rowId
            | HandleKind.FieldDefinition -> entityHandleFromTable TableIndex.Field rowId
            | HandleKind.Parameter -> entityHandleFromTable TableIndex.Param rowId
            | HandleKind.ModuleDefinition -> entityHandleFromTable TableIndex.Module rowId
            | HandleKind.StandaloneSignature -> entityHandleFromTable TableIndex.StandAloneSig rowId
            | HandleKind.InterfaceImplementation -> entityHandleFromTable TableIndex.InterfaceImpl rowId
            | HandleKind.ModuleReference -> entityHandleFromTable TableIndex.ModuleRef rowId
            | HandleKind.AssemblyDefinition -> entityHandleFromTable TableIndex.Assembly rowId
            | HandleKind.GenericParameter -> entityHandleFromTable TableIndex.GenericParam rowId
            | HandleKind.GenericParameterConstraint -> entityHandleFromTable TableIndex.GenericParamConstraint rowId
            | HandleKind.MethodSpecification -> entityHandleFromTable TableIndex.MethodSpec rowId
            | _ -> invalidArg (nameof kind) "Unsupported custom attribute reference"

        for row in customAttributeRows do
            tableMirror.AddCustomAttributeRow row

            let parentHandle =
                let struct (kind, rowId) = row.Parent
                entityHandleOf kind rowId

            let ctorHandle =
                let struct (kind, rowId) = row.Constructor
                entityHandleOf kind rowId

            if emitSrmTables then
                let blobHandle = metadataBuilder.GetOrAddBlob row.Value
                metadataBuilder.AddCustomAttribute(parentHandle, ctorHandle, blobHandle) |> ignore

            encLog.Add(struct (TableIndex.CustomAttribute, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.CustomAttribute, row.RowId))

        for row in propertyDefinitionRows do
            if row.IsAdded then
                if emitSrmTables then
                    let nameHandle = metadataBuilder.GetOrAddString row.Name
                    let signatureHandle = metadataBuilder.GetOrAddBlob row.Signature
                    metadataBuilder.AddProperty(row.Attributes, nameHandle, signatureHandle) |> ignore
                tableMirror.AddPropertyRow row

                encLog.Add(struct (TableIndex.Property, row.RowId, EditAndContinueOperation.AddProperty))
                encMap.Add(struct (TableIndex.Property, row.RowId))

        for row in eventDefinitionRows do
            if row.IsAdded then
                if emitSrmTables then
                    let nameHandle = metadataBuilder.GetOrAddString row.Name
                    let typeHandle = row.EventType
                    metadataBuilder.AddEvent(row.Attributes, nameHandle, typeHandle) |> ignore
                tableMirror.AddEventRow row

                encLog.Add(struct (TableIndex.Event, row.RowId, EditAndContinueOperation.AddEvent))
                encMap.Add(struct (TableIndex.Event, row.RowId))

        for row in propertyMapRows do
            if row.IsAdded then
                if emitSrmTables then
                    let parentHandle = MetadataTokens.TypeDefinitionHandle row.TypeDefRowId
                    let propertyListHandle =
                        match row.FirstPropertyRowId with
                        | Some deltaRowId -> MetadataTokens.PropertyDefinitionHandle deltaRowId
                        | None -> invalidOp "Property map rows marked as added require a property list pointer."
                    metadataBuilder.AddPropertyMap(parentHandle, propertyListHandle) |> ignore

                encLog.Add(struct (TableIndex.PropertyMap, row.RowId, EditAndContinueOperation.AddProperty))
                encMap.Add(struct (TableIndex.PropertyMap, row.RowId))
                tableMirror.AddPropertyMapRow row

        for row in eventMapRows do
            if row.IsAdded then
                if emitSrmTables then
                    let parentHandle = MetadataTokens.TypeDefinitionHandle row.TypeDefRowId
                    let eventListHandle =
                        match row.FirstEventRowId with
                        | Some deltaRowId -> MetadataTokens.EventDefinitionHandle deltaRowId
                        | None -> invalidOp "Event map rows marked as added require an event list pointer."
                    metadataBuilder.AddEventMap(parentHandle, eventListHandle) |> ignore

                encLog.Add(struct (TableIndex.EventMap, row.RowId, EditAndContinueOperation.AddEvent))
                encMap.Add(struct (TableIndex.EventMap, row.RowId))
                tableMirror.AddEventMapRow row

        for row in methodSemanticsRows do
            if row.IsAdded then
                let methodRowId = row.MethodToken &&& 0x00FFFFFF
                let methodHandle = MetadataTokens.MethodDefinitionHandle methodRowId
                metadataBuilder.AddMethodSemantics(row.Association, row.Attributes, methodHandle) |> ignore
                tableMirror.AddMethodSemanticsRow row

                encLog.Add(struct (TableIndex.MethodSemantics, row.RowId, EditAndContinueOperation.AddMethod))
                encMap.Add(struct (TableIndex.MethodSemantics, row.RowId))

        for _, newToken, literal in userStringUpdates do
            let offset = newToken &&& 0x00FFFFFF
            tableMirror.AddUserStringLiteral(offset, literal)

        let debugRows =
            [ for index in Enum.GetValues(typeof<TableIndex>) |> Seq.cast<TableIndex> do
                  let count = metadataBuilder.GetRowCount index
                  if count <> 0 then yield index, count ]

        let allowedTables =
            set
                [ TableIndex.Module
                  TableIndex.MethodDef
                  TableIndex.Param
                  TableIndex.TypeRef
                  TableIndex.MemberRef
                  TableIndex.AssemblyRef
                  TableIndex.CustomAttribute
                  TableIndex.StandAloneSig
                  TableIndex.Property
                  TableIndex.Event
                  TableIndex.PropertyMap
                  TableIndex.EventMap
                  TableIndex.MethodSemantics
                  TableIndex.EncLog
                  TableIndex.EncMap ]

        let unexpectedTables =
            debugRows
            |> List.filter (fun (index, _) -> not (allowedTables.Contains index))

        if not (List.isEmpty unexpectedTables) then
            let details =
                unexpectedTables
                |> List.map (fun (index, count) -> sprintf "%A:%d" index count)
                |> String.concat ", "
            failwithf "Unexpected rows in delta metadata: %s" details

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
            let handle = MetadataTokens.Handle(tableIndex, rowId)
            metadataBuilder.AddEncLogEntry(handle, operation) |> ignore
            tableMirror.AddEncLogRow(tableIndex, rowId, operation)

        for struct (tableIndex, rowId) in encMapEntries do
            let handle = MetadataTokens.Handle(tableIndex, rowId)
            metadataBuilder.AddEncMapEntry(handle) |> ignore
            tableMirror.AddEncMapRow(tableIndex, rowId)

        let metadataSizes = DeltaMetadataSerializer.computeMetadataSizes tableMirror normalizedExternalRowCounts
        let tableRowCounts = metadataSizes.RowCounts
        let tableBitMasks = metadataSizes.BitMasks
        let heapSizes = metadataSizes.HeapSizes
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

        { Metadata = metadataBytes
          StringHeap = heapStreams.Strings
          BlobHeap = heapStreams.Blobs
          GuidHeap = heapStreams.Guids
          EncLog = encLogEntries |> Array.map (fun struct (a, b, c) -> (a, b, c))
          EncMap = encMapEntries |> Array.map (fun struct (a, b) -> (a, b))
          TableRowCounts = tableRowCounts
          HeapSizes = heapSizes
          HeapOffsets = heapOffsets
          Tables = tableMirror.TableRows
          TableBitMasks = tableBitMasks
          IndexSizes = indexSizes
          TableStream = tableStream }

let emitWithReferences
    (metadataBuilder: MetadataBuilder)
    (moduleName: string)
    (moduleNameHandle: StringHandle option)
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
        metadataBuilder
        moduleName
        moduleNameHandle
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
    (metadataBuilder: MetadataBuilder)
    (moduleName: string)
    (moduleNameHandle: StringHandle option)
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
        metadataBuilder
        moduleName
        moduleNameHandle
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
