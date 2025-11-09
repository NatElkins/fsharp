module internal FSharp.Compiler.CodeGen.DeltaMetadataTables

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinary
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.HotReloadBaseline

/// Mirrors a subset of the AbstractIL metadata table writer so we can build delta
/// rows without relying on System.Reflection.Metadata.MetadataBuilder. Today the
/// tables are populated alongside the SRM builder so we can validate row counts
/// and capture the RowElement payloads; a future change will serialize these
/// tables directly via the AbstractIL writer.
type DeltaMetadataTables(metadataReader: MetadataReader) =
    let strings = MetadataTable<string>.New("#Strings", HashIdentity.Structural)
    let blobs = MetadataTable<byte[]>.New("#Blob", HashIdentity.Structural)
    let guids = MetadataTable<byte[]>.New("#Guid", HashIdentity.Structural)

    let moduleTable = MetadataTable<UnsharedRow>.New("Module", HashIdentity.Structural)
    let methodTable = MetadataTable<UnsharedRow>.New("MethodDef", HashIdentity.Structural)
    let paramTable = MetadataTable<UnsharedRow>.New("Param", HashIdentity.Structural)
    let propertyTable = MetadataTable<UnsharedRow>.New("Property", HashIdentity.Structural)
    let eventTable = MetadataTable<UnsharedRow>.New("Event", HashIdentity.Structural)
    let propertyMapTable = MetadataTable<UnsharedRow>.New("PropertyMap", HashIdentity.Structural)
    let eventMapTable = MetadataTable<UnsharedRow>.New("EventMap", HashIdentity.Structural)
    let methodSemanticsTable = MetadataTable<UnsharedRow>.New("MethodSemantics", HashIdentity.Structural)

    let inline addStringHandle (handle: StringHandle) =
        if handle.IsNil then
            0
        else
            strings.FindOrAddSharedEntry(metadataReader.GetString handle)

    let inline addStringValue (value: string) =
        if isNull value then 0 else strings.FindOrAddSharedEntry value

    let inline addBlobHandle (handle: BlobHandle) =
        if handle.IsNil then 0 else blobs.FindOrAddSharedEntry(metadataReader.GetBlobBytes handle)

    let inline addBlobBytes (bytes: byte[]) =
        if isNull (box bytes) || bytes.Length = 0 then 0 else blobs.FindOrAddSharedEntry(bytes)

    let inline addGuidValue (value: Guid) =
        if value = Guid.Empty then
            0
        else
            guids.FindOrAddSharedEntry(value.ToByteArray())

    let inline encodeTypeDefOrRef (handle: EntityHandle) =
        if handle.IsNil then
            tdor_TypeDef, 0
        else
            match handle.Kind with
            | HandleKind.TypeDefinition ->
                tdor_TypeDef, MetadataTokens.GetRowNumber(TypeDefinitionHandle.op_Explicit handle)
            | HandleKind.TypeReference ->
                tdor_TypeRef, MetadataTokens.GetRowNumber(TypeReferenceHandle.op_Explicit handle)
            | HandleKind.TypeSpecification ->
                tdor_TypeSpec, MetadataTokens.GetRowNumber(TypeSpecificationHandle.op_Explicit handle)
            | _ -> tdor_TypeDef, 0

    member _.AddModuleRow(name: string, moduleId: Guid, encId: Guid, encBaseId: Guid) =
        if moduleTable.Count = 0 then
            let nameIdx = addStringValue name
            let mvidIdx = addGuidValue moduleId
            let encIdx = addGuidValue encId
            let encBaseIdx = addGuidValue encBaseId
            let row =
                [|
                    UShort 0us
                    StringE nameIdx
                    Guid mvidIdx
                    Guid encIdx
                    Guid encBaseIdx
                |]
                |> UnsharedRow
            moduleTable.AddUnsharedEntry row |> ignore

    member _.AddMethodRow
        (
            row: MethodDefinitionRowInfo,
            methodDef: MethodDefinition,
            body: MethodBodyUpdate,
            firstParamRowId: int option
        )
        =
        let nameIdx = addStringHandle methodDef.Name
        let sigIdx = addBlobHandle methodDef.Signature
        let paramListIdx = firstParamRowId |> Option.defaultValue 0
        let rowElements =
            [|
                ULong body.CodeOffset
                UShort(uint16 methodDef.ImplAttributes)
                UShort(uint16 methodDef.Attributes)
                StringE nameIdx
                Blob sigIdx
                SimpleIndex(TableNames.Param, paramListIdx)
            |]
            |> UnsharedRow
        methodTable.AddUnsharedEntry rowElements |> ignore

    member _.AddParameterRow(row: ParameterDefinitionRowInfo, parameter: Parameter) =
        let nameIdx = addStringHandle parameter.Name
        let rowElements =
            [|
                UShort(uint16 parameter.Attributes)
                UShort(parameter.SequenceNumber)
                StringE nameIdx
            |]
            |> UnsharedRow
        paramTable.AddUnsharedEntry rowElements |> ignore

    member _.AddPropertyRow(row: PropertyMetadataUpdate) =
        let propertyDef = metadataReader.GetPropertyDefinition row.Handle
        let nameIdx = addStringHandle propertyDef.Name
        let sigIdx = addBlobHandle propertyDef.Signature
        let rowElements =
            [|
                UShort(uint16 propertyDef.Attributes)
                StringE nameIdx
                Blob sigIdx
            |]
            |> UnsharedRow
        propertyTable.AddUnsharedEntry rowElements |> ignore

    member _.AddEventRow(row: EventMetadataUpdate) =
        let eventDef = metadataReader.GetEventDefinition row.Handle
        let nameIdx = addStringHandle eventDef.Name
        let tdorTag, tdorRow = encodeTypeDefOrRef eventDef.Type
        let rowElements =
            [|
                UShort(uint16 eventDef.Attributes)
                StringE nameIdx
                TypeDefOrRefOrSpec(tdorTag, tdorRow)
            |]
            |> UnsharedRow
        eventTable.AddUnsharedEntry rowElements |> ignore

    member _.AddPropertyMapRow(row: PropertyMapRowInfo) =
        let propertyList = row.FirstPropertyRowId |> Option.defaultValue 0
        let rowElements =
            [|
                SimpleIndex(TableNames.TypeDef, row.TypeDefRowId)
                SimpleIndex(TableNames.Property, propertyList)
            |]
            |> UnsharedRow
        propertyMapTable.AddUnsharedEntry rowElements |> ignore

    member _.AddEventMapRow(row: EventMapRowInfo) =
        let eventList = row.FirstEventRowId |> Option.defaultValue 0
        let rowElements =
            [|
                SimpleIndex(TableNames.TypeDef, row.TypeDefRowId)
                SimpleIndex(TableNames.Event, eventList)
            |]
            |> UnsharedRow
        eventMapTable.AddUnsharedEntry rowElements |> ignore

    member _.AddMethodSemanticsRow(row: MethodSemanticsMetadataUpdate) =
        let methodHandle = MetadataTokens.MethodDefinitionHandle row.MethodToken
        let methodRowId = MetadataTokens.GetRowNumber methodHandle
        let assocTag, assocRowId =
            match row.AssociationInfo with
            | Some(MethodSemanticsAssociation.PropertyAssociation(_, propertyRowId)) -> hs_Property, propertyRowId
            | Some(MethodSemanticsAssociation.EventAssociation(_, eventRowId)) -> hs_Event, eventRowId
            | None ->
                match row.Association.Kind with
                | HandleKind.PropertyDefinition ->
                    let handle = PropertyDefinitionHandle.op_Explicit row.Association
                    hs_Property, MetadataTokens.GetRowNumber handle
                | HandleKind.EventDefinition ->
                    let handle = EventDefinitionHandle.op_Explicit row.Association
                    hs_Event, MetadataTokens.GetRowNumber handle
                | _ -> hs_Property, 0
        let rowElements =
            [|
                UShort(uint16 row.Attributes)
                SimpleIndex(TableNames.Method, methodRowId)
                HasSemantics(assocTag, assocRowId)
            |]
            |> UnsharedRow
        methodSemanticsTable.AddUnsharedEntry rowElements |> ignore

    member _.TableRowCounts : int[] =
        let counts = Array.zeroCreate MetadataTokens.TableCount
        counts[int TableIndex.Module] <- moduleTable.Count
        counts[int TableIndex.MethodDef] <- methodTable.Count
        counts[int TableIndex.Param] <- paramTable.Count
        counts[int TableIndex.Property] <- propertyTable.Count
        counts[int TableIndex.Event] <- eventTable.Count
        counts[int TableIndex.PropertyMap] <- propertyMapTable.Count
        counts[int TableIndex.EventMap] <- eventMapTable.Count
        counts[int TableIndex.MethodSemantics] <- methodSemanticsTable.Count
        counts
