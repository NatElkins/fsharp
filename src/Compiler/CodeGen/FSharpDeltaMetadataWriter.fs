module internal FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.HotReloadBaseline

let private shouldTraceMetadata () =
    match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METADATA") with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

type MethodDefinitionRowInfo =
    {
        Key: MethodDefinitionKey
        RowId: int
        IsAdded: bool
    }

type ParameterDefinitionRowInfo =
    {
        Key: ParameterDefinitionKey
        RowId: int
        IsAdded: bool
        ParameterHandle: ParameterHandle option
    }

type MethodMetadataUpdate =
    {
        MethodKey: MethodDefinitionKey
        MethodToken: int
        MethodHandle: MethodDefinitionHandle
        Body: MethodBodyUpdate
    }

type PropertyMetadataUpdate =
    {
        Key: PropertyDefinitionKey
        RowId: int
        IsAdded: bool
        Handle: PropertyDefinitionHandle
    }

type EventMetadataUpdate =
    {
        Key: EventDefinitionKey
        RowId: int
        IsAdded: bool
        Handle: EventDefinitionHandle
    }

type PropertyMapRowInfo =
    {
        DeclaringType: string
        RowId: int
        TypeDefRowId: int
        FirstPropertyRowId: int option
        IsAdded: bool
    }

type EventMapRowInfo =
    {
        DeclaringType: string
        RowId: int
        TypeDefRowId: int
        FirstEventRowId: int option
        IsAdded: bool
    }

type MethodSemanticsMetadataUpdate =
    {
        RowId: int
        Association: EntityHandle
        MethodToken: int
        Attributes: System.Reflection.MethodSemanticsAttributes
        IsAdded: bool
        AssociationInfo: MethodSemanticsAssociation option
    }

type MetadataDelta =
    {
        Metadata: byte[]
        EncLog: (TableIndex * int * EditAndContinueOperation) array
        EncMap: (TableIndex * int) array
        TableRowCounts: int[]
        HeapSizes: MetadataHeapSizes
    }

let emit
    (metadataBuilder: MetadataBuilder)
    (metadataReader: MetadataReader)
    (encId: Guid)
    (encBaseId: Guid)
    (moduleId: Guid)
    (methodDefinitionRows: MethodDefinitionRowInfo list)
    (parameterDefinitionRows: ParameterDefinitionRowInfo list)
    (propertyDefinitionRows: PropertyMetadataUpdate list)
    (eventDefinitionRows: EventMetadataUpdate list)
    (propertyMapRows: PropertyMapRowInfo list)
    (eventMapRows: EventMapRowInfo list)
    (methodSemanticsRows: MethodSemanticsMetadataUpdate list)
    (updates: MethodMetadataUpdate list)
    : MetadataDelta =
    if shouldTraceMetadata () then
        printfn "[fsharp-hotreload][metadata-writer] emit invoked updates=%d" (List.length updates)
    if List.isEmpty updates then
        let emptyHeapSizes =
            { StringHeapSize = 0
              UserStringHeapSize = 0
              BlobHeapSize = 0
              GuidHeapSize = 0 }

        { Metadata = Array.empty
          EncLog = Array.empty
          EncMap = Array.empty
          TableRowCounts = Array.zeroCreate MetadataTokens.TableCount
          HeapSizes = emptyHeapSizes }
    else

        // Ensure tables not emitted in the current delta remain empty to satisfy metadata writer invariants.
        let methodUpdateCount = methodDefinitionRows |> List.length
        let parameterUpdateCount = parameterDefinitionRows |> List.length
        let propertyUpdateCount = propertyDefinitionRows |> List.length
        let eventUpdateCount = eventDefinitionRows |> List.length
        let propertyMapLogCount = propertyMapRows |> List.length
        let propertyMapAddCount = propertyMapRows |> List.filter (fun row -> row.IsAdded) |> List.length
        let eventMapLogCount = eventMapRows |> List.length
        let eventMapAddCount = eventMapRows |> List.filter (fun row -> row.IsAdded) |> List.length
        let methodSemanticsUpdateCount = methodSemanticsRows |> List.length

        metadataBuilder.SetCapacity(TableIndex.Module, 1)
        metadataBuilder.SetCapacity(TableIndex.TypeRef, 0)
        metadataBuilder.SetCapacity(TableIndex.TypeDef, 0)
        metadataBuilder.SetCapacity(TableIndex.Field, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodDef, methodUpdateCount)
        metadataBuilder.SetCapacity(TableIndex.Param, parameterUpdateCount)
        metadataBuilder.SetCapacity(TableIndex.InterfaceImpl, 0)
        metadataBuilder.SetCapacity(TableIndex.MemberRef, 0)
        metadataBuilder.SetCapacity(TableIndex.Constant, 0)
        metadataBuilder.SetCapacity(TableIndex.CustomAttribute, 0)
        metadataBuilder.SetCapacity(TableIndex.FieldMarshal, 0)
        metadataBuilder.SetCapacity(TableIndex.DeclSecurity, 0)
        metadataBuilder.SetCapacity(TableIndex.ClassLayout, 0)
        metadataBuilder.SetCapacity(TableIndex.FieldLayout, 0)
        metadataBuilder.SetCapacity(TableIndex.StandAloneSig, 0)
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
            1
            + methodUpdateCount
            + parameterUpdateCount
            + propertyUpdateCount
            + eventUpdateCount
            + propertyMapLogCount
            + eventMapLogCount
            + methodSemanticsUpdateCount
        metadataBuilder.SetCapacity(TableIndex.EncLog, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.EncMap, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.Assembly, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyOS, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRef, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefOS, 0)
        metadataBuilder.SetCapacity(TableIndex.File, 0)
        metadataBuilder.SetCapacity(TableIndex.ExportedType, 0)
        metadataBuilder.SetCapacity(TableIndex.ManifestResource, 0)
        metadataBuilder.SetCapacity(TableIndex.NestedClass, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParam, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodSpec, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParamConstraint, 0)

        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleName = metadataReader.GetString moduleDef.Name
        let moduleNameHandle = metadataBuilder.GetOrAddString(moduleName)
        let mvidHandle = metadataBuilder.GetOrAddGuid(moduleId)
        let encIdHandle = metadataBuilder.GetOrAddGuid(encId)
        let encBaseHandle = metadataBuilder.GetOrAddGuid(encBaseId)
        let moduleHandle = metadataBuilder.AddModule(0, moduleNameHandle, mvidHandle, encIdHandle, encBaseHandle)

        let updatesByKey = Dictionary<MethodDefinitionKey, MethodMetadataUpdate>(HashIdentity.Structural)
        for update in updates do
            updatesByKey[update.MethodKey] <- update

        let mutable encLog = ResizeArray()
        let mutable encMap = ResizeArray()

        metadataBuilder.AddEncLogEntry(moduleHandle, EditAndContinueOperation.Default) |> ignore
        metadataBuilder.AddEncMapEntry(moduleHandle) |> ignore
        let moduleRowId = MetadataTokens.GetRowNumber moduleHandle
        encLog.Add(struct (TableIndex.Module, moduleRowId, EditAndContinueOperation.Default))
        encMap.Add(struct (TableIndex.Module, moduleRowId))

        for row in methodDefinitionRows do
            match updatesByKey.TryGetValue row.Key with
            | true, update ->
                let methodDef = metadataReader.GetMethodDefinition update.MethodHandle

                let methodName = metadataReader.GetString methodDef.Name
                let nameHandle = metadataBuilder.GetOrAddString methodName

                let signatureBytes = metadataReader.GetBlobBytes methodDef.Signature
                let signatureHandle = metadataBuilder.GetOrAddBlob signatureBytes

                metadataBuilder.AddMethodDefinition(
                    methodDef.Attributes,
                    methodDef.ImplAttributes,
                    nameHandle,
                    signatureHandle,
                    update.Body.CodeOffset,
                    ParameterHandle()
                )
                |> ignore

                let methodHandle = MetadataTokens.MethodDefinitionHandle row.RowId
                let operation = if row.IsAdded then EditAndContinueOperation.AddMethod else EditAndContinueOperation.Default
                metadataBuilder.AddEncLogEntry(methodHandle, operation) |> ignore
                metadataBuilder.AddEncMapEntry(methodHandle) |> ignore
                encLog.Add(struct (TableIndex.MethodDef, row.RowId, operation))
                encMap.Add(struct (TableIndex.MethodDef, row.RowId))
            | _ ->
                if shouldTraceMetadata () then
                    printfn "[fsharp-hotreload][metadata-writer] missing update payload for %A" row.Key

        for row in parameterDefinitionRows do
            match row.ParameterHandle with
            | Some handle ->
                let parameter = metadataReader.GetParameter handle
                let nameHandle: StringHandle =
                    if parameter.Name.IsNil then
                        StringHandle()
                    else
                        metadataBuilder.GetOrAddString(metadataReader.GetString parameter.Name)
                let sequenceNumber = int parameter.SequenceNumber

                metadataBuilder.AddParameter(parameter.Attributes, nameHandle, sequenceNumber) |> ignore

                let parameterHandle = MetadataTokens.ParameterHandle row.RowId
                let operation = if row.IsAdded then EditAndContinueOperation.AddParameter else EditAndContinueOperation.Default
                metadataBuilder.AddEncLogEntry(parameterHandle, operation) |> ignore
                metadataBuilder.AddEncMapEntry(parameterHandle) |> ignore
                encLog.Add(struct (TableIndex.Param, row.RowId, operation))
                encMap.Add(struct (TableIndex.Param, row.RowId))
            | None ->
                failwith "Added parameter rows require parameter metadata payload."

        for row in propertyDefinitionRows do
            let propertyDef = metadataReader.GetPropertyDefinition row.Handle
            let propertyName = metadataReader.GetString propertyDef.Name
            let nameHandle = metadataBuilder.GetOrAddString propertyName
            let signatureBytes = metadataReader.GetBlobBytes propertyDef.Signature
            let signatureHandle = metadataBuilder.GetOrAddBlob signatureBytes

            metadataBuilder.AddProperty(propertyDef.Attributes, nameHandle, signatureHandle) |> ignore

            let propertyHandle = MetadataTokens.PropertyDefinitionHandle row.RowId
            let operation = if row.IsAdded then EditAndContinueOperation.AddProperty else EditAndContinueOperation.Default
            metadataBuilder.AddEncLogEntry(propertyHandle, operation) |> ignore
            metadataBuilder.AddEncMapEntry(propertyHandle) |> ignore
            encLog.Add(struct (TableIndex.Property, row.RowId, operation))
            encMap.Add(struct (TableIndex.Property, row.RowId))

        for row in eventDefinitionRows do
            let eventDef = metadataReader.GetEventDefinition row.Handle
            let eventName = metadataReader.GetString eventDef.Name
            let nameHandle = metadataBuilder.GetOrAddString eventName
            let typeHandle = eventDef.Type

            metadataBuilder.AddEvent(eventDef.Attributes, nameHandle, typeHandle) |> ignore

            let eventHandle = MetadataTokens.EventDefinitionHandle row.RowId
            let operation = if row.IsAdded then EditAndContinueOperation.AddEvent else EditAndContinueOperation.Default
            metadataBuilder.AddEncLogEntry(eventHandle, operation) |> ignore
            metadataBuilder.AddEncMapEntry(eventHandle) |> ignore
            encLog.Add(struct (TableIndex.Event, row.RowId, operation))
            encMap.Add(struct (TableIndex.Event, row.RowId))

        for row in propertyMapRows do
            let handle = MetadataTokens.EntityHandle(TableIndex.PropertyMap, row.RowId)
            if row.IsAdded then
                let parentHandle = MetadataTokens.TypeDefinitionHandle row.TypeDefRowId
                let propertyListHandle =
                    match row.FirstPropertyRowId with
                    | Some deltaRowId -> MetadataTokens.PropertyDefinitionHandle deltaRowId
                    | None -> invalidOp "Property map rows marked as added require a property list pointer."
                metadataBuilder.AddPropertyMap(parentHandle, propertyListHandle) |> ignore

            metadataBuilder.AddEncLogEntry(handle, EditAndContinueOperation.Default) |> ignore
            metadataBuilder.AddEncMapEntry(handle) |> ignore
            encLog.Add(struct (TableIndex.PropertyMap, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.PropertyMap, row.RowId))

        for row in eventMapRows do
            let handle = MetadataTokens.EntityHandle(TableIndex.EventMap, row.RowId)
            if row.IsAdded then
                let parentHandle = MetadataTokens.TypeDefinitionHandle row.TypeDefRowId
                let eventListHandle =
                    match row.FirstEventRowId with
                    | Some deltaRowId -> MetadataTokens.EventDefinitionHandle deltaRowId
                    | None -> invalidOp "Event map rows marked as added require an event list pointer."
                metadataBuilder.AddEventMap(parentHandle, eventListHandle) |> ignore

            metadataBuilder.AddEncLogEntry(handle, EditAndContinueOperation.Default) |> ignore
            metadataBuilder.AddEncMapEntry(handle) |> ignore
            encLog.Add(struct (TableIndex.EventMap, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.EventMap, row.RowId))

        for row in methodSemanticsRows do
            if row.IsAdded then
                let methodRowId = row.MethodToken &&& 0x00FFFFFF
                let methodHandle = MetadataTokens.MethodDefinitionHandle methodRowId
                metadataBuilder.AddMethodSemantics(row.Association, row.Attributes, methodHandle) |> ignore

            let semanticsHandle =
                MetadataTokens.Handle(TableIndex.MethodSemantics, row.RowId)
                |> EntityHandle.op_Explicit

            metadataBuilder.AddEncLogEntry(semanticsHandle, EditAndContinueOperation.Default) |> ignore
            metadataBuilder.AddEncMapEntry(semanticsHandle) |> ignore
            encLog.Add(struct (TableIndex.MethodSemantics, row.RowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.MethodSemantics, row.RowId))

        let debugRows =
            [ for index in Enum.GetValues(typeof<TableIndex>) |> Seq.cast<TableIndex> do
                  let count = metadataBuilder.GetRowCount index
                  if count <> 0 then yield index, count ]

        let allowedTables =
            set
                [ TableIndex.Module
                  TableIndex.MethodDef
                  TableIndex.Param
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

        let metadataRoot = new MetadataRootBuilder(metadataBuilder)
        let metadataBlob = BlobBuilder()
        try
            metadataRoot.Serialize(metadataBlob, 0, 0)
        with ex ->
            let counts =
                [ for index in Enum.GetValues(typeof<TableIndex>) |> Seq.cast<TableIndex> do
                      yield index, metadataBuilder.GetRowCount index ]
                |> List.filter (fun (_, count) -> count <> 0)
            let details = counts |> List.map (fun (i, c) -> sprintf "%A:%d" i c) |> String.concat ", "
            let enriched = sprintf "Metadata serialization failed. Non-zero tables: %s" details
            raise (Exception(enriched, ex))

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange(metadataBlob.ToArray()))
        let deltaReader = deltaProvider.GetMetadataReader()

        let tableRowCounts = Array.zeroCreate MetadataTokens.TableCount
        tableRowCounts.[int TableIndex.MethodDef] <- methodUpdateCount
        tableRowCounts.[int TableIndex.Param] <- parameterUpdateCount
        tableRowCounts.[int TableIndex.Property] <- propertyUpdateCount
        tableRowCounts.[int TableIndex.Event] <- eventUpdateCount
        tableRowCounts.[int TableIndex.PropertyMap] <- propertyMapAddCount
        tableRowCounts.[int TableIndex.EventMap] <- eventMapAddCount
        tableRowCounts.[int TableIndex.MethodSemantics] <- methodSemanticsUpdateCount

        let heapSizes =
            { StringHeapSize = deltaReader.GetHeapSize HeapIndex.String
              UserStringHeapSize = deltaReader.GetHeapSize HeapIndex.UserString
              BlobHeapSize = deltaReader.GetHeapSize HeapIndex.Blob
              GuidHeapSize = deltaReader.GetHeapSize HeapIndex.Guid }

        if shouldTraceMetadata () then
            printfn "[fsharp-hotreload][metadata-writer] tableCounts method=%d param=%d" methodUpdateCount parameterUpdateCount

        { Metadata = metadataBlob.ToArray()
          EncLog = encLog |> Seq.toArray |> Array.map (fun struct (a, b, c) -> (a, b, c))
          EncMap = encMap |> Seq.toArray |> Array.map (fun struct (a, b) -> (a, b))
          TableRowCounts = tableRowCounts
          HeapSizes = heapSizes }
