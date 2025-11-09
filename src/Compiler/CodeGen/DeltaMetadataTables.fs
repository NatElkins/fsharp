module internal FSharp.Compiler.CodeGen.DeltaMetadataTables

open System
open System.IO
open System.Reflection.Metadata
open System.Text
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinary
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.HotReloadBaseline

/// Mirrors the AbstractIL metadata tables for the subset of rows emitted by
/// hot reload deltas. The tables are populated alongside the SRM metadata
/// builder so we can eventually serialize deltas directly via AbstractIL.
type DeltaMetadataTables() =
    let utf8 = Encoding.UTF8
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

    let inline addStringValue (value: string) =
        if String.IsNullOrEmpty value then 0 else strings.FindOrAddSharedEntry value

    let inline addStringOption (value: string option) =
        match value with
        | Some v when not (String.IsNullOrEmpty v) -> strings.FindOrAddSharedEntry v
        | _ -> 0

    let inline addBlobBytes (bytes: byte[]) =
        if obj.ReferenceEquals(bytes, null) || bytes.Length = 0 then 0 else blobs.FindOrAddSharedEntry bytes

    let inline addGuidValue (value: Guid) =
        if value = Guid.Empty then 0 else guids.FindOrAddSharedEntry(value.ToByteArray())

    let inline encodeTypeDefOrRef (handle: EntityHandle) =
        if handle.IsNil then
            tdor_TypeDef, 0
        else
            match handle.Kind with
            | HandleKind.TypeDefinition -> tdor_TypeDef, MetadataTokens.GetRowNumber(TypeDefinitionHandle.op_Explicit handle)
            | HandleKind.TypeReference -> tdor_TypeRef, MetadataTokens.GetRowNumber(TypeReferenceHandle.op_Explicit handle)
            | HandleKind.TypeSpecification -> tdor_TypeSpec, MetadataTokens.GetRowNumber(TypeSpecificationHandle.op_Explicit handle)
            | _ -> tdor_TypeDef, 0

    member _.AddModuleRow(name: string, moduleId: Guid, encId: Guid, encBaseId: Guid) =
        if moduleTable.Count = 0 then
            let row =
                [|
                    UShort 0us
                    StringE(addStringValue name)
                    Guid(addGuidValue moduleId)
                    Guid(addGuidValue encId)
                    Guid(addGuidValue encBaseId)
                |]
                |> UnsharedRow
            moduleTable.AddUnsharedEntry row |> ignore

    member _.AddMethodRow(row: MethodDefinitionRowInfo, body: MethodBodyUpdate) =
        let rowElements =
            [|
                ULong body.CodeOffset
                UShort(uint16 row.ImplAttributes)
                UShort(uint16 row.Attributes)
                StringE(addStringValue row.Name)
                Blob(addBlobBytes row.Signature)
                SimpleIndex(TableNames.Param, row.FirstParameterRowId |> Option.defaultValue 0)
            |]
            |> UnsharedRow
        methodTable.AddUnsharedEntry rowElements |> ignore

    member _.AddParameterRow(row: ParameterDefinitionRowInfo) =
        let nameIdx = addStringOption row.Name
        let rowElements =
            [|
                UShort(uint16 row.Attributes)
                UShort(uint16 row.SequenceNumber)
                StringE nameIdx
            |]
            |> UnsharedRow
        paramTable.AddUnsharedEntry rowElements |> ignore

    member _.AddPropertyRow(row: PropertyDefinitionRowInfo) =
        let rowElements =
            [|
                UShort(uint16 row.Attributes)
                StringE(addStringValue row.Name)
                Blob(addBlobBytes row.Signature)
            |]
            |> UnsharedRow
        propertyTable.AddUnsharedEntry rowElements |> ignore

    member _.AddEventRow(row: EventDefinitionRowInfo) =
        let tdorTag, tdorRow = encodeTypeDefOrRef row.EventType
        let rowElements =
            [|
                UShort(uint16 row.Attributes)
                StringE(addStringValue row.Name)
                TypeDefOrRefOrSpec(tdorTag, tdorRow)
            |]
            |> UnsharedRow
        eventTable.AddUnsharedEntry rowElements |> ignore

    member _.AddPropertyMapRow(row: PropertyMapRowInfo) =
        let rowElements =
            [|
                SimpleIndex(TableNames.TypeDef, row.TypeDefRowId)
                SimpleIndex(TableNames.Property, row.FirstPropertyRowId |> Option.defaultValue 0)
            |]
            |> UnsharedRow
        propertyMapTable.AddUnsharedEntry rowElements |> ignore

    member _.AddEventMapRow(row: EventMapRowInfo) =
        let rowElements =
            [|
                SimpleIndex(TableNames.TypeDef, row.TypeDefRowId)
                SimpleIndex(TableNames.Event, row.FirstEventRowId |> Option.defaultValue 0)
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
                | HandleKind.PropertyDefinition -> hs_Property, MetadataTokens.GetRowNumber(PropertyDefinitionHandle.op_Explicit row.Association)
                | HandleKind.EventDefinition -> hs_Event, MetadataTokens.GetRowNumber(EventDefinitionHandle.op_Explicit row.Association)
                | _ -> hs_Property, 0
        let rowElements =
            [|
                UShort(uint16 row.Attributes)
                SimpleIndex(TableNames.Method, methodRowId)
                HasSemantics(assocTag, assocRowId)
            |]
            |> UnsharedRow
        methodSemanticsTable.AddUnsharedEntry rowElements |> ignore

    let inline compressedLength size =
        if size <= 0x7F then 1
        elif size <= 0x3FFF then 2
        else 4

    let mutable stringHeapBytesCache: byte[] option = None
    let mutable blobHeapBytesCache: byte[] option = None
    let mutable guidHeapBytesCache: byte[] option = None

    let writeCompressedUnsigned (writer: BinaryWriter) (value: int) =
        if value <= 0x7F then
            writer.Write(byte value)
        elif value <= 0x3FFF then
            let b1 = byte ((value >>> 8) ||| 0x80)
            let b0 = byte (value &&& 0xFF)
            writer.Write(b1)
            writer.Write(b0)
        elif value <= 0x1FFFFFFF then
            let b2 = byte ((value >>> 24) ||| 0xC0)
            let b1 = byte ((value >>> 16) &&& 0xFF)
            let b0 = byte ((value >>> 8) &&& 0xFF)
            let bLowest = byte (value &&& 0xFF)
            writer.Write(b2)
            writer.Write(b1)
            writer.Write(b0)
            writer.Write(bLowest)
        else
            invalidArg (nameof value) "Compressed integer is too large for CLI metadata."

    let buildStringHeapBytes () =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        writer.Write(byte 0) // heap starts with empty string
        for entry in strings.Entries do
            if not (String.IsNullOrEmpty entry) then
                let bytes = utf8.GetBytes(entry)
                writer.Write(bytes)
            writer.Write(byte 0)
        writer.Flush()
        ms.ToArray()

    let buildBlobHeapBytes () =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        writer.Write(byte 0)
        for entry in blobs.Entries do
            writeCompressedUnsigned writer entry.Length
            if entry.Length > 0 then
                writer.Write(entry)
        writer.Flush()
        ms.ToArray()

    let buildGuidHeapBytes () =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        // Guid heap index 0 refers to null; keep a single 0 GUID there.
        writer.Write(Array.zeroCreate<byte> 16)
        for entry in guids.Entries do
            if entry.Length = 16 then
                writer.Write(entry)
            else
                invalidArg "entry" "GUID entries must be 16 bytes."
        writer.Flush()
        ms.ToArray()

    member _.StringHeapBytes
        with get () =
            match stringHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildStringHeapBytes ()
                stringHeapBytesCache <- Some bytes
                bytes

    member _.BlobHeapBytes
        with get () =
            match blobHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildBlobHeapBytes ()
                blobHeapBytesCache <- Some bytes
                bytes

    member _.GuidHeapBytes
        with get () =
            match guidHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildGuidHeapBytes ()
                guidHeapBytesCache <- Some bytes
                bytes

    member _.StringHeapSize = _.StringHeapBytes.Length

    member _.BlobHeapSize = _.BlobHeapBytes.Length

    member _.GuidHeapSize = _.GuidHeapBytes.Length

    member _.HeapSizes : MetadataHeapSizes =
        { StringHeapSize = _.StringHeapSize
          UserStringHeapSize = 0
          BlobHeapSize = _.BlobHeapSize
          GuidHeapSize = _.GuidHeapSize }

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
