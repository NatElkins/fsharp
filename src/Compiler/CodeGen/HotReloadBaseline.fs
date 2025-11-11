module internal FSharp.Compiler.HotReloadBaseline

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxGen
open FSharp.Compiler.Syntax.PrettyNaming

let private tableCount = MetadataTokens.TableCount

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
        EncBaseId = moduleId
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
        TableEntriesAdded = Array.zeroCreate tableCount
        StringStreamLengthAdded = 0
        UserStringStreamLengthAdded = 0
        BlobStreamLengthAdded = 0
        GuidStreamLengthAdded = 0
        AddedOrChangedMethods = []
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
        let updatedHeapSizes =
            { StringHeapSize = baseline.Metadata.HeapSizes.StringHeapSize + deltaHeapSizes.StringHeapSize
              UserStringHeapSize = baseline.Metadata.HeapSizes.UserStringHeapSize + deltaHeapSizes.UserStringHeapSize
              BlobHeapSize = baseline.Metadata.HeapSizes.BlobHeapSize + deltaHeapSizes.BlobHeapSize
              GuidHeapSize = baseline.Metadata.HeapSizes.GuidHeapSize + deltaHeapSizes.GuidHeapSize }

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
        TableEntriesAdded = updatedTableEntries
        StringStreamLengthAdded = baseline.StringStreamLengthAdded + deltaHeapSizes.StringHeapSize
        UserStringStreamLengthAdded = baseline.UserStringStreamLengthAdded + deltaHeapSizes.UserStringHeapSize
        BlobStreamLengthAdded = baseline.BlobStreamLengthAdded + deltaHeapSizes.BlobHeapSize
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
        Array.init MetadataTokens.TableCount (fun i ->
            let tableIndex = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte i)
            reader.GetTableRowCount(tableIndex))

    { HeapSizes = heapSizes
      TableRowCounts = tableCounts
      GuidHeapStart = heapSizes.GuidHeapSize }
