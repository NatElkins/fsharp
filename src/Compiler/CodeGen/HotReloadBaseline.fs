module internal FSharp.Compiler.HotReloadBaseline

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILDeltaHandles
open FSharp.Compiler.IlxGen

module ILBaselineReader = FSharp.Compiler.AbstractIL.ILBaselineReader
open FSharp.Compiler.Syntax.PrettyNaming

let private tableCount = DeltaTokens.TableCount

let private traceHeapOffsets =
    lazy (
        match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_HEAP_OFFSETS") with
        | null | "" -> false
        | value -> value = "1" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
    )

/// Align a size to a 4-byte boundary (stream alignment per ECMA-335).
/// Used for Blob and UserString heap cumulative tracking, per Roslyn behavior.
let private align4 value = (value + 3) &&& ~~~3

/// <summary>Metadata describing a method body that was added or changed in a delta.</summary>
type AddedOrChangedMethodInfo =
    {
        MethodToken: int
        LocalSignatureToken: int
        CodeOffset: int
        CodeLength: int
    }

/// <summary>Stable identifier for a method definition used when correlating baseline tokens.</summary>
type MethodDefinitionKey =
    {
        DeclaringType: string
        Name: string
        GenericArity: int
        ParameterTypes: ILType list
        ReturnType: ILType
    }

/// Baseline metadata handles reused to keep heap offsets stable across deltas.

/// <summary>Stable identifier for a method parameter (sequence number within a method).</summary>
type ParameterDefinitionKey =
    {
        Method: MethodDefinitionKey
        SequenceNumber: int
    }

/// <summary>Stable identifier for a field definition in the baseline assembly.</summary>
type FieldDefinitionKey =
    {
        DeclaringType: string
        Name: string
        FieldType: ILType
    }

/// <summary>Stable identifier for a property definition (including indexer parameter shapes).</summary>
type PropertyDefinitionKey =
    {
        DeclaringType: string
        Name: string
        PropertyType: ILType
        IndexParameterTypes: ILType list
    }

/// <summary>Stable identifier for an event definition in the baseline assembly.</summary>
type EventDefinitionKey =
    {
        DeclaringType: string
        Name: string
        EventType: ILType option
    }

type MethodDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      SignatureOffset: BlobOffset option
      FirstParameterRowId: int option
      Rva: int option
      Attributes: MethodAttributes option
      ImplAttributes: MethodImplAttributes option }

type TypeReferenceKey =
    { Scope: string
      Namespace: string
      Name: string }

type ParameterDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      RowId: int option }

type PropertyDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      SignatureOffset: BlobOffset option }

type EventDefinitionMetadataHandles = { NameOffset: StringOffset option }

type BaselineHandleCache =
    { MethodHandles: Map<MethodDefinitionKey, MethodDefinitionMetadataHandles>
      ParameterHandles: Map<ParameterDefinitionKey, ParameterDefinitionMetadataHandles>
      PropertyHandles: Map<PropertyDefinitionKey, PropertyDefinitionMetadataHandles>
      EventHandles: Map<EventDefinitionKey, EventDefinitionMetadataHandles> }

    static member Empty =
        { MethodHandles = Map.empty
          ParameterHandles = Map.empty
          PropertyHandles = Map.empty
          EventHandles = Map.empty }

type MethodSemanticsAssociation =
    | PropertyAssociation of PropertyDefinitionKey * rowId:int
    | EventAssociation of EventDefinitionKey * rowId:int

type MethodSemanticsEntry =
    {
        RowId: int
        Attributes: MethodSemanticsAttributes
        Association: MethodSemanticsAssociation
    }

/// <summary>Portable PDB snapshot captured during baseline emission.</summary>
type PortablePdbSnapshot =
    {
        Bytes: byte[]
        TableRowCounts: ImmutableArray<int>
        EntryPointToken: int option
    }

/// <summary>
/// Represents the captured state of a baseline emission, mirroring Roslyn's EmitBaseline. It stores metadata
/// snapshots along with stable token maps so delta emission can reuse pre-existing metadata handles.
/// </summary>
type FSharpEmitBaseline =
    {
        ModuleId: Guid
        EncId: Guid
        EncBaseId: Guid
        NextGeneration: int
        ModuleNameOffset: StringOffset option
        Metadata: MetadataSnapshot
        TokenMappings: ILTokenMappings
        TypeTokens: Map<string, int>
        MethodTokens: Map<MethodDefinitionKey, int>
        FieldTokens: Map<FieldDefinitionKey, int>
        PropertyTokens: Map<PropertyDefinitionKey, int>
        EventTokens: Map<EventDefinitionKey, int>
        PropertyMapEntries: Map<string, int>
        EventMapEntries: Map<string, int>
        MethodSemanticsEntries: Map<MethodDefinitionKey, MethodSemanticsEntry list>
        IlxGenEnvironment: IlxGenEnvSnapshot option
        PortablePdb: PortablePdbSnapshot option
        SynthesizedNameSnapshot: Map<string, string[]>
        MetadataHandles: BaselineHandleCache
        TypeReferenceTokens: Map<TypeReferenceKey, int>
        AssemblyReferenceTokens: Map<string, int>
        TableEntriesAdded: int[]
        StringStreamLengthAdded: int
        UserStringStreamLengthAdded: int
        BlobStreamLengthAdded: int
        GuidStreamLengthAdded: int
        AddedOrChangedMethods: AddedOrChangedMethodInfo list
    }

type private BaselineMaps =
    {
        TypeTokens: Map<string, int>
        MethodTokens: Map<MethodDefinitionKey, int>
        FieldTokens: Map<FieldDefinitionKey, int>
        PropertyTokens: Map<PropertyDefinitionKey, int>
        EventTokens: Map<EventDefinitionKey, int>
        PropertyMapEntries: Map<string, int>
        EventMapEntries: Map<string, int>
    }

let private emptyMaps =
    {
        TypeTokens = Map.empty
        MethodTokens = Map.empty
        FieldTokens = Map.empty
        PropertyTokens = Map.empty
        EventTokens = Map.empty
        PropertyMapEntries = Map.empty
        EventMapEntries = Map.empty
    }

let private collectSynthesizedNameSnapshot (ilModule: ILModuleDef) =
    let buckets = Dictionary<string, ResizeArray<string>>(StringComparer.Ordinal)

    let recordName (name: string) =
        if not (String.IsNullOrWhiteSpace name) && IsCompilerGeneratedName name then
            let basicName = GetBasicNameOfPossibleCompilerGeneratedName name
            if not (String.IsNullOrWhiteSpace basicName) then
                let bucket =
                    match buckets.TryGetValue basicName with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<string>()
                        buckets[basicName] <- created
                        created

                if not (bucket.Contains name) then
                    bucket.Add(name)

    let rec collectTypeDef (typeDef: ILTypeDef) =
        recordName typeDef.Name

        typeDef.Fields.AsList()
        |> List.iter (fun fieldDef -> recordName fieldDef.Name)

        typeDef.Methods.AsList()
        |> List.iter (fun methodDef -> recordName methodDef.Name)

        typeDef.Properties.AsList()
        |> List.iter (fun propertyDef -> recordName propertyDef.Name)

        typeDef.Events.AsList()
        |> List.iter (fun eventDef -> recordName eventDef.Name)

        typeDef.NestedTypes.AsList()
        |> List.iter collectTypeDef

    ilModule.TypeDefs.AsList()
    |> List.iter collectTypeDef

    buckets
    |> Seq.map (fun (KeyValue(key, bucket)) -> key, bucket.ToArray())
    |> Map.ofSeq

/// <summary>
/// Populate the baseline token maps by walking type definitions and their nested members.
/// </summary>
let rec private collectType
    (tokenMappings: ILTokenMappings)
    (scope: ILScopeRef)
    (enclosing: ILTypeDef list)
    (maps: BaselineMaps)
    (tdef: ILTypeDef)
    : BaselineMaps =
    let typeRef = mkRefForNestedILTypeDef scope (enclosing, tdef)
    let typeName = typeRef.FullName
    let typeToken = tokenMappings.TypeDefTokenMap(enclosing, tdef)

    let maps =
        { maps with
            TypeTokens = maps.TypeTokens |> Map.add typeName typeToken
        }

    let maps =
        tdef.Methods.AsList()
        |> List.fold
            (fun (acc: BaselineMaps) mdef ->
                let key =
                    {
                        DeclaringType = typeName
                        Name = mdef.Name
                        GenericArity = mdef.GenericParams.Length
                        ParameterTypes = mdef.ParameterTypes
                        ReturnType = mdef.Return.Type
                    }

                let token = tokenMappings.MethodDefTokenMap (enclosing, tdef) mdef

                { acc with
                    MethodTokens = acc.MethodTokens |> Map.add key token
                })
            maps

    let maps =
        tdef.Fields.AsList()
        |> List.fold
            (fun (acc: BaselineMaps) fdef ->
                let key =
                    {
                        DeclaringType = typeName
                        Name = fdef.Name
                        FieldType = fdef.FieldType
                    }

                let token = tokenMappings.FieldDefTokenMap (enclosing, tdef) fdef

                { acc with
                    FieldTokens = acc.FieldTokens |> Map.add key token
                })
            maps

    let propertyDefs = tdef.Properties.AsList()

    let maps =
        propertyDefs
        |> List.fold
            (fun (acc: BaselineMaps) pdef ->
                let key =
                    {
                        DeclaringType = typeName
                        Name = pdef.Name
                        PropertyType = pdef.PropertyType
                        IndexParameterTypes = List.ofSeq pdef.Args
                    }

                let token = tokenMappings.PropertyTokenMap (enclosing, tdef) pdef

                { acc with
                    PropertyTokens = acc.PropertyTokens |> Map.add key token
                })
            maps

    let maps =
        match propertyDefs with
        | first :: _ ->
            let token = tokenMappings.PropertyTokenMap (enclosing, tdef) first
            let rowId = token &&& 0x00FFFFFF
            { maps with PropertyMapEntries = maps.PropertyMapEntries |> Map.add typeName rowId }
        | [] -> maps

    let eventDefs = tdef.Events.AsList()

    let maps =
        eventDefs
        |> List.fold
            (fun (acc: BaselineMaps) edef ->
                let key =
                    {
                        DeclaringType = typeName
                        Name = edef.Name
                        EventType = edef.EventType
                    }

                let token = tokenMappings.EventTokenMap (enclosing, tdef) edef

                { acc with
                    EventTokens = acc.EventTokens |> Map.add key token
                })
            maps

    let maps =
        match eventDefs with
        | first :: _ ->
            let token = tokenMappings.EventTokenMap (enclosing, tdef) first
            let rowId = token &&& 0x00FFFFFF
            { maps with EventMapEntries = maps.EventMapEntries |> Map.add typeName rowId }
        | [] -> maps

    tdef.NestedTypes.AsList()
    |> List.fold (collectType tokenMappings scope (enclosing @ [ tdef ])) maps

let private methodKeyFromRef (methodRef: ILMethodRef) =
    { MethodDefinitionKey.DeclaringType = methodRef.DeclaringTypeRef.FullName
      Name = methodRef.Name
      GenericArity = methodRef.GenericArity
      ParameterTypes = methodRef.ArgTypes |> Seq.toList
      ReturnType = methodRef.ReturnType }

let collectMethodSemanticsEntries
    (ilModule: ILModuleDef)
    (methodTokens: Map<MethodDefinitionKey, int>)
    (propertyTokens: Map<PropertyDefinitionKey, int>)
    (eventTokens: Map<EventDefinitionKey, int>)
    =
    let entries = Dictionary<MethodDefinitionKey, ResizeArray<MethodSemanticsEntry>>(HashIdentity.Structural)
    let mutable nextRowId = 0

    let addEntry methodKey entry =
        match entries.TryGetValue methodKey with
        | true, bucket -> bucket.Add entry
        | _ ->
            let bucket = ResizeArray()
            bucket.Add entry
            entries[methodKey] <- bucket

    let tryAddSemantics association attributes methodRefOpt =
        match methodRefOpt with
        | None -> ()
        | Some methodRef ->
            let methodKey = methodKeyFromRef methodRef
            if methodTokens.ContainsKey methodKey then
                nextRowId <- nextRowId + 1
                addEntry methodKey
                    { RowId = nextRowId
                      Attributes = attributes
                      Association = association }

    let rec visitType enclosing (typeDef: ILTypeDef) =
        let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
        let typeName = typeRef.FullName

        let buildPropertyKey (prop: ILPropertyDef) =
            { PropertyDefinitionKey.DeclaringType = typeName
              Name = prop.Name
              PropertyType = prop.PropertyType
              IndexParameterTypes = List.ofSeq prop.Args }

        let buildEventKey (eventDef: ILEventDef) =
            { EventDefinitionKey.DeclaringType = typeName
              Name = eventDef.Name
              EventType = eventDef.EventType }

        for prop in typeDef.Properties.AsList() do
            let propertyKey = buildPropertyKey prop
            match propertyTokens |> Map.tryFind propertyKey with
            | Some propertyToken ->
                let rowId = propertyToken &&& 0x00FFFFFF
                let association = MethodSemanticsAssociation.PropertyAssociation(propertyKey, rowId)
                tryAddSemantics association MethodSemanticsAttributes.Setter prop.SetMethod
                tryAddSemantics association MethodSemanticsAttributes.Getter prop.GetMethod
            | None -> ()

        for eventDef in typeDef.Events.AsList() do
            let eventKey = buildEventKey eventDef
            match eventTokens |> Map.tryFind eventKey with
            | Some eventToken ->
                let rowId = eventToken &&& 0x00FFFFFF
                let association = MethodSemanticsAssociation.EventAssociation(eventKey, rowId)
                tryAddSemantics association MethodSemanticsAttributes.Adder (Some eventDef.AddMethod)
                tryAddSemantics association MethodSemanticsAttributes.Remover (Some eventDef.RemoveMethod)
                eventDef.FireMethod |> Option.iter (fun fire -> tryAddSemantics association MethodSemanticsAttributes.Raiser (Some fire))
                eventDef.OtherMethods |> List.iter (fun other -> tryAddSemantics association MethodSemanticsAttributes.Other (Some other))
            | None -> ()

        typeDef.NestedTypes.AsList()
        |> List.iter (fun nested -> visitType (enclosing @ [ typeDef ]) nested)

    ilModule.TypeDefs.AsList()
    |> List.iter (visitType [])

    entries
    |> Seq.map (fun kvp -> kvp.Key, kvp.Value |> Seq.toList)
    |> Map.ofSeq

let private createCore
    (moduleId: Guid)
    (ilModule: ILModuleDef)
    (tokenMappings: ILTokenMappings)
    (metadataSnapshot: MetadataSnapshot)
    (ilxGenEnvironment: IlxGenEnvSnapshot option)
    (portablePdbSnapshot: PortablePdbSnapshot option)
    =
    let scope = ILScopeRef.Local

    let maps =
        ilModule.TypeDefs.AsList()
        |> List.fold (collectType tokenMappings scope []) emptyMaps

    let methodSemanticsEntries =
        collectMethodSemanticsEntries ilModule maps.MethodTokens maps.PropertyTokens maps.EventTokens

    let synthesizedNames = collectSynthesizedNameSnapshot ilModule

    {
        ModuleId = moduleId
        EncId = System.Guid.Empty
        EncBaseId = System.Guid.Empty
        NextGeneration = 1
        Metadata = metadataSnapshot
        TokenMappings = tokenMappings
        TypeTokens = maps.TypeTokens
        MethodTokens = maps.MethodTokens
        FieldTokens = maps.FieldTokens
        PropertyTokens = maps.PropertyTokens
        EventTokens = maps.EventTokens
        PropertyMapEntries = maps.PropertyMapEntries
        EventMapEntries = maps.EventMapEntries
        MethodSemanticsEntries = methodSemanticsEntries
        IlxGenEnvironment = ilxGenEnvironment
        PortablePdb = portablePdbSnapshot
        SynthesizedNameSnapshot = synthesizedNames
        MetadataHandles = BaselineHandleCache.Empty
        TypeReferenceTokens = Map.empty
        AssemblyReferenceTokens = Map.empty
        TableEntriesAdded = Array.zeroCreate tableCount
        StringStreamLengthAdded = 0
        UserStringStreamLengthAdded = 0
        BlobStreamLengthAdded = 0
        GuidStreamLengthAdded = 0
        AddedOrChangedMethods = []
        ModuleNameOffset = None
    }

let internal applyDelta
    (baseline: FSharpEmitBaseline)
    (deltaTableCounts: int[])
    (deltaHeapSizes: MetadataHeapSizes)
    (addedOrChangedMethods: AddedOrChangedMethodInfo list)
    (encId: Guid)
    (encBaseId: Guid)
    (synthesizedSnapshot: Map<string, string[]> option)
    : FSharpEmitBaseline =

    let tableCounts =
        if deltaTableCounts.Length = tableCount then
            deltaTableCounts
        else
            Array.zeroCreate tableCount

    let updatedTableEntries =
        Array.init tableCount (fun i ->
            let previous = baseline.TableEntriesAdded[i]
            previous + tableCounts.[i])

    let updatedMetadataSnapshot =
        // Per Roslyn DeltaMetadataWriter.cs: Blob and UserString streams are concatenated
        // aligned to 4-byte boundaries; String stream is concatenated unaligned.
        let updatedHeapSizes =
            { StringHeapSize = baseline.Metadata.HeapSizes.StringHeapSize + deltaHeapSizes.StringHeapSize
              UserStringHeapSize = baseline.Metadata.HeapSizes.UserStringHeapSize + align4 deltaHeapSizes.UserStringHeapSize
              BlobHeapSize = baseline.Metadata.HeapSizes.BlobHeapSize + align4 deltaHeapSizes.BlobHeapSize
              GuidHeapSize = baseline.Metadata.HeapSizes.GuidHeapSize + deltaHeapSizes.GuidHeapSize }

        if traceHeapOffsets.Value then
            printfn "[fsharp-hotreload][heap-offsets] applyDelta: Updating baseline heap sizes"
            printfn "[fsharp-hotreload][heap-offsets]   Before: UserStringHeapSize = %d" baseline.Metadata.HeapSizes.UserStringHeapSize
            printfn "[fsharp-hotreload][heap-offsets]   Delta:  UserStringHeapSize = %d (aligned = %d)" deltaHeapSizes.UserStringHeapSize (align4 deltaHeapSizes.UserStringHeapSize)
            printfn "[fsharp-hotreload][heap-offsets]   After:  UserStringHeapSize = %d" updatedHeapSizes.UserStringHeapSize
            printfn "[fsharp-hotreload][heap-offsets]   Generation: %d -> %d" baseline.NextGeneration (baseline.NextGeneration + 1)

        let updatedTableCountsAbsolute =
            Array.init tableCount (fun i ->
                baseline.Metadata.TableRowCounts.[i] + tableCounts.[i])

        { baseline.Metadata with
            HeapSizes = updatedHeapSizes
            TableRowCounts = updatedTableCountsAbsolute }

    { baseline with
        EncId = encId
        EncBaseId = encBaseId
        NextGeneration = baseline.NextGeneration + 1
        ModuleNameOffset = baseline.ModuleNameOffset
        TableEntriesAdded = updatedTableEntries
        // Per Roslyn DeltaMetadataWriter.cs: String stream is concatenated unaligned,
        // Blob and UserString streams are concatenated aligned to 4-byte boundaries.
        StringStreamLengthAdded = baseline.StringStreamLengthAdded + deltaHeapSizes.StringHeapSize
        UserStringStreamLengthAdded = baseline.UserStringStreamLengthAdded + align4 deltaHeapSizes.UserStringHeapSize
        BlobStreamLengthAdded = baseline.BlobStreamLengthAdded + align4 deltaHeapSizes.BlobHeapSize
        GuidStreamLengthAdded = baseline.GuidStreamLengthAdded + deltaHeapSizes.GuidHeapSize
        Metadata = updatedMetadataSnapshot
        SynthesizedNameSnapshot =
            match synthesizedSnapshot with
            | Some snapshot -> snapshot
            | None -> baseline.SynthesizedNameSnapshot
        MethodSemanticsEntries = baseline.MethodSemanticsEntries
        AddedOrChangedMethods =
            (addedOrChangedMethods @ baseline.AddedOrChangedMethods)
            |> List.distinctBy (fun info -> info.MethodToken)
        TypeReferenceTokens = baseline.TypeReferenceTokens
        AssemblyReferenceTokens = baseline.AssemblyReferenceTokens
    }

/// <summary>Create an <see cref="FSharpEmitBaseline"/> without capturing the ILX environment snapshot.</summary>
let create
    (ilModule: ILModuleDef)
    (tokenMappings: ILTokenMappings)
    (metadataSnapshot: MetadataSnapshot)
    (moduleId: Guid)
    (portablePdbSnapshot: PortablePdbSnapshot option)
    =
    createCore moduleId ilModule tokenMappings metadataSnapshot None portablePdbSnapshot

/// <summary>Create an <see cref="FSharpEmitBaseline"/> that carries the captured ILX environment snapshot.</summary>
let createWithEnvironment
    (ilModule: ILModuleDef)
    (tokenMappings: ILTokenMappings)
    (metadataSnapshot: MetadataSnapshot)
    (ilxGenEnvironment: IlxGenEnvSnapshot)
    (moduleId: Guid)
    (portablePdbSnapshot: PortablePdbSnapshot option)
    =
    createCore moduleId ilModule tokenMappings metadataSnapshot (Some ilxGenEnvironment) portablePdbSnapshot

let metadataSnapshotFromReader (reader: MetadataReader) =
    let heapSizes =
        { StringHeapSize = reader.GetHeapSize(HeapIndex.String)
          UserStringHeapSize = reader.GetHeapSize(HeapIndex.UserString)
          BlobHeapSize = reader.GetHeapSize(HeapIndex.Blob)
          GuidHeapSize = reader.GetHeapSize(HeapIndex.Guid) }

    let tableCounts =
        Array.init tableCount (fun i ->
            let tableIndex = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte i)
            reader.GetTableRowCount(tableIndex))

    { HeapSizes = heapSizes
      TableRowCounts = tableCounts
      GuidHeapStart = heapSizes.GuidHeapSize }

let private stringOffsetOption (handle: StringHandle) =
    if handle.IsNil then None else Some (StringOffset (MetadataTokens.GetHeapOffset handle))

let private blobOffsetOption (handle: BlobHandle) =
    if handle.IsNil then None else Some (BlobOffset (MetadataTokens.GetHeapOffset handle))

let private buildMethodHandles (reader: MetadataReader) (methodTokens: Map<MethodDefinitionKey, int>) : Map<MethodDefinitionKey, MethodDefinitionMetadataHandles> =
    methodTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let handle = MetadataTokens.MethodDefinitionHandle token
        if handle.IsNil then
            None
        else
            let methodDef = reader.GetMethodDefinition handle
            let parameters = methodDef.GetParameters()
            let mutable firstParamRowId = None
            for parameterHandle in parameters do
                if firstParamRowId.IsNone then
                    let rowId = MetadataTokens.GetRowNumber parameterHandle
                    if rowId > 0 then
                        firstParamRowId <- Some rowId
            Some(
                key,
                { NameOffset = stringOffsetOption methodDef.Name
                  SignatureOffset = blobOffsetOption methodDef.Signature
                  FirstParameterRowId = firstParamRowId
                  Rva = Some methodDef.RelativeVirtualAddress
                  Attributes = Some methodDef.Attributes
                  ImplAttributes = Some methodDef.ImplAttributes })
    )
    |> Map.ofSeq

let private buildParameterHandles
    (reader: MetadataReader)
    (methodTokens: Map<MethodDefinitionKey, int>)
    : Map<ParameterDefinitionKey, ParameterDefinitionMetadataHandles>
    =
    methodTokens
    |> Seq.collect (fun kvp ->
        let methodKey = kvp.Key
        let token = kvp.Value
        let methodHandle = MetadataTokens.MethodDefinitionHandle token
        if methodHandle.IsNil then
            Seq.empty
        else
            let methodDef = reader.GetMethodDefinition methodHandle
            methodDef.GetParameters()
            |> Seq.map (fun parameterHandle ->
                let parameter = reader.GetParameter parameterHandle
                let key =
                    { ParameterDefinitionKey.Method = methodKey
                      SequenceNumber = int parameter.SequenceNumber }
                key,
                ({ NameOffset = stringOffsetOption parameter.Name
                   RowId = Some(MetadataTokens.GetRowNumber parameterHandle) } : ParameterDefinitionMetadataHandles))
    )
    |> Map.ofSeq

let private buildPropertyHandles (reader: MetadataReader) (propertyTokens: Map<PropertyDefinitionKey, int>) : Map<PropertyDefinitionKey, PropertyDefinitionMetadataHandles> =
    propertyTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let handle = MetadataTokens.PropertyDefinitionHandle token
        if handle.IsNil then
            None
        else
            let propertyDef = reader.GetPropertyDefinition handle
            Some(
                key,
                { NameOffset = stringOffsetOption propertyDef.Name
                  SignatureOffset = blobOffsetOption propertyDef.Signature }) )
    |> Map.ofSeq

let private buildEventHandles (reader: MetadataReader) (eventTokens: Map<EventDefinitionKey, int>) : Map<EventDefinitionKey, EventDefinitionMetadataHandles> =
    eventTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let handle = MetadataTokens.EventDefinitionHandle token
        if handle.IsNil then
            None
        else
            let eventDef = reader.GetEventDefinition handle
            Some(key, ({ NameOffset = stringOffsetOption eventDef.Name } : EventDefinitionMetadataHandles)) )
    |> Map.ofSeq

let private buildAssemblyReferenceTokens (reader: MetadataReader) : Map<string, int> =
    reader.AssemblyReferences
    |> Seq.map (fun handle ->
        let assemblyRef = reader.GetAssemblyReference handle
        let name = reader.GetString assemblyRef.Name
        let token = MetadataTokens.GetToken(EntityHandle.op_Implicit handle)
        name, token)
    |> Map.ofSeq

let private buildTypeReferenceTokens (reader: MetadataReader) : Map<TypeReferenceKey, int> =
    reader.TypeReferences
    |> Seq.choose (fun handle ->
        let typeRef = reader.GetTypeReference handle
        let name = reader.GetString typeRef.Name
        let namespaceName = if typeRef.Namespace.IsNil then "" else reader.GetString typeRef.Namespace
        match typeRef.ResolutionScope.Kind with
        | HandleKind.AssemblyReference ->
            let assemblyHandle = AssemblyReferenceHandle.op_Explicit typeRef.ResolutionScope
            let assemblyRef = reader.GetAssemblyReference assemblyHandle
            let scopeName = reader.GetString assemblyRef.Name
            let key =
                { TypeReferenceKey.Scope = scopeName
                  Namespace = namespaceName
                  Name = name }
            let token = MetadataTokens.GetToken(EntityHandle.op_Implicit handle)
            Some(key, token)
        | _ -> None)
    |> Map.ofSeq

let attachMetadataHandles (metadataReader: MetadataReader) (baseline: FSharpEmitBaseline) =
    let methodHandles = buildMethodHandles metadataReader baseline.MethodTokens
    let parameterHandles = buildParameterHandles metadataReader baseline.MethodTokens
    let propertyHandles = buildPropertyHandles metadataReader baseline.PropertyTokens
    let eventHandles = buildEventHandles metadataReader baseline.EventTokens
    let typeReferenceTokens = buildTypeReferenceTokens metadataReader
    let assemblyReferenceTokens = buildAssemblyReferenceTokens metadataReader
    let cache =
        { MethodHandles = methodHandles
          ParameterHandles = parameterHandles
          PropertyHandles = propertyHandles
          EventHandles = eventHandles }
    let moduleDef = metadataReader.GetModuleDefinition()
    { baseline with
        MetadataHandles = cache
        ModuleNameOffset = stringOffsetOption moduleDef.Name
        TypeReferenceTokens = typeReferenceTokens
        AssemblyReferenceTokens = assemblyReferenceTokens }

// ============================================================================
// Byte-based functions using ILBaselineReader (no SRM dependency)
// ============================================================================

/// Extract metadata snapshot from PE file bytes without using SRM.
let metadataSnapshotFromBytes (bytes: byte[]) : MetadataSnapshot option =
    ILBaselineReader.metadataSnapshotFromBytes bytes

/// Read Module.Mvid GUID from PE file bytes without using SRM.
let readModuleMvid (bytes: byte[]) : Guid option =
    ILBaselineReader.readModuleMvidFromBytes bytes

/// Build method handles from baseline using ILBaselineReader.
let private buildMethodHandlesFromBytes (reader: ILBaselineReader.BaselineMetadataReader) (methodTokens: Map<MethodDefinitionKey, int>) : Map<MethodDefinitionKey, MethodDefinitionMetadataHandles> =
    methodTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let rowId = token &&& 0x00FFFFFF
        match reader.GetMethodDef(rowId) with
        | None -> None
        | Some methodDef ->
            let firstParamRowId =
                match reader.GetMethodParamRange(rowId) with
                | Some (first, _) -> Some first
                | None -> None
            let result : MethodDefinitionMetadataHandles =
                { NameOffset = if methodDef.NameOffset = 0 then None else Some (StringOffset methodDef.NameOffset)
                  SignatureOffset = if methodDef.SignatureOffset = 0 then None else Some (BlobOffset methodDef.SignatureOffset)
                  FirstParameterRowId = firstParamRowId
                  Rva = Some methodDef.RVA
                  Attributes = Some (LanguagePrimitives.EnumOfValue<int, MethodAttributes> methodDef.Flags)
                  ImplAttributes = Some (LanguagePrimitives.EnumOfValue<int, MethodImplAttributes> methodDef.ImplFlags) }
            Some(key, result)
    )
    |> Map.ofSeq

/// Build parameter handles from baseline using ILBaselineReader.
let private buildParameterHandlesFromBytes
    (reader: ILBaselineReader.BaselineMetadataReader)
    (methodTokens: Map<MethodDefinitionKey, int>)
    : Map<ParameterDefinitionKey, ParameterDefinitionMetadataHandles>
    =
    methodTokens
    |> Seq.collect (fun kvp ->
        let methodKey = kvp.Key
        let token = kvp.Value
        let methodRowId = token &&& 0x00FFFFFF
        match reader.GetMethodParamRange(methodRowId) with
        | None -> Seq.empty
        | Some (firstParam, lastParam) ->
            seq {
                for paramRowId in firstParam..lastParam do
                    match reader.GetParam(paramRowId) with
                    | None -> ()
                    | Some param ->
                        let key =
                            { ParameterDefinitionKey.Method = methodKey
                              SequenceNumber = param.Sequence }
                        let result : ParameterDefinitionMetadataHandles =
                            { NameOffset = if param.NameOffset = 0 then None else Some (StringOffset param.NameOffset)
                              RowId = Some paramRowId }
                        yield key, result
            }
    )
    |> Map.ofSeq

/// Build property handles from baseline using ILBaselineReader.
let private buildPropertyHandlesFromBytes (reader: ILBaselineReader.BaselineMetadataReader) (propertyTokens: Map<PropertyDefinitionKey, int>) : Map<PropertyDefinitionKey, PropertyDefinitionMetadataHandles> =
    propertyTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let rowId = token &&& 0x00FFFFFF
        match reader.GetProperty(rowId) with
        | None -> None
        | Some prop ->
            let result : PropertyDefinitionMetadataHandles =
                { NameOffset = if prop.NameOffset = 0 then None else Some (StringOffset prop.NameOffset)
                  SignatureOffset = if prop.SignatureOffset = 0 then None else Some (BlobOffset prop.SignatureOffset) }
            Some(key, result)
    )
    |> Map.ofSeq

/// Build event handles from baseline using ILBaselineReader.
let private buildEventHandlesFromBytes (reader: ILBaselineReader.BaselineMetadataReader) (eventTokens: Map<EventDefinitionKey, int>) : Map<EventDefinitionKey, EventDefinitionMetadataHandles> =
    eventTokens
    |> Seq.choose (fun kvp ->
        let key = kvp.Key
        let token = kvp.Value
        let rowId = token &&& 0x00FFFFFF
        match reader.GetEvent(rowId) with
        | None -> None
        | Some event ->
            let result : EventDefinitionMetadataHandles =
                { NameOffset = if event.NameOffset = 0 then None else Some (StringOffset event.NameOffset) }
            Some(key, result)
    )
    |> Map.ofSeq

/// Build assembly reference tokens from baseline using ILBaselineReader.
let private buildAssemblyReferenceTokensFromBytes (reader: ILBaselineReader.BaselineMetadataReader) : Map<string, int> =
    seq {
        for rowId in 1..reader.AssemblyRefCount do
            match reader.GetAssemblyRef(rowId) with
            | Some assemblyRef ->
                let name = reader.GetString(assemblyRef.NameOffset)
                // AssemblyRef table index is 0x23, token = (0x23 << 24) | rowId
                let token = (0x23 <<< 24) ||| rowId
                yield name, token
            | None -> ()
    }
    |> Map.ofSeq

/// Build type reference tokens from baseline using ILBaselineReader.
let private buildTypeReferenceTokensFromBytes (reader: ILBaselineReader.BaselineMetadataReader) : Map<TypeReferenceKey, int> =
    seq {
        for rowId in 1..reader.TypeRefCount do
            match reader.GetTypeRef(rowId) with
            | Some typeRef ->
                let (tableIndex, scopeRowId) = reader.DecodeResolutionScope(typeRef.ResolutionScope)
                // Only include TypeRefs with AssemblyRef scope (tableIndex = 35)
                if tableIndex = 35 then
                    match reader.GetAssemblyRef(scopeRowId) with
                    | Some assemblyRef ->
                        let scopeName = reader.GetString(assemblyRef.NameOffset)
                        let name = reader.GetString(typeRef.NameOffset)
                        let namespaceName = reader.GetString(typeRef.NamespaceOffset)
                        let key =
                            { TypeReferenceKey.Scope = scopeName
                              Namespace = namespaceName
                              Name = name }
                        // TypeRef table index is 0x01, token = (0x01 << 24) | rowId
                        let token = (0x01 <<< 24) ||| rowId
                        yield key, token
                    | None -> ()
            | None -> ()
    }
    |> Map.ofSeq

/// Attach metadata handles from PE bytes without using SRM MetadataReader.
let attachMetadataHandlesFromBytes (bytes: byte[]) (baseline: FSharpEmitBaseline) : FSharpEmitBaseline =
    match ILBaselineReader.BaselineMetadataReader.Create(bytes) with
    | None -> baseline  // Return unchanged if we can't read the metadata
    | Some reader ->
        let methodHandles = buildMethodHandlesFromBytes reader baseline.MethodTokens
        let parameterHandles = buildParameterHandlesFromBytes reader baseline.MethodTokens
        let propertyHandles = buildPropertyHandlesFromBytes reader baseline.PropertyTokens
        let eventHandles = buildEventHandlesFromBytes reader baseline.EventTokens
        let typeReferenceTokens = buildTypeReferenceTokensFromBytes reader
        let assemblyReferenceTokens = buildAssemblyReferenceTokensFromBytes reader
        let cache =
            { MethodHandles = methodHandles
              ParameterHandles = parameterHandles
              PropertyHandles = propertyHandles
              EventHandles = eventHandles }
        let moduleNameOffset =
            match reader.GetModule() with
            | Some m when m.NameOffset > 0 -> Some (StringOffset m.NameOffset)
            | _ -> None
        { baseline with
            MetadataHandles = cache
            ModuleNameOffset = moduleNameOffset
            TypeReferenceTokens = typeReferenceTokens
            AssemblyReferenceTokens = assemblyReferenceTokens }
