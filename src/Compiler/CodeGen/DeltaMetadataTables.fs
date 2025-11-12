module internal FSharp.Compiler.CodeGen.DeltaMetadataTables

open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Text
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.CodeGen.DeltaMetadataTypes

/// Mirrors the AbstractIL metadata tables for the subset of rows emitted by
/// hot reload deltas. The tables are populated alongside the SRM metadata
/// builder so we can eventually serialize deltas directly via AbstractIL.
type MetadataHeapOffsets =
    {
        StringHeapStart: int
        BlobHeapStart: int
        GuidHeapStart: int
        UserStringHeapStart: int
    }

    static member Zero =
        { StringHeapStart = 0
          BlobHeapStart = 0
          GuidHeapStart = 0
          UserStringHeapStart = 0 }

    static member OfHeapSizes(heapSizes: MetadataHeapSizes) =
        { StringHeapStart = heapSizes.StringHeapSize
          BlobHeapStart = heapSizes.BlobHeapSize
          GuidHeapStart = heapSizes.GuidHeapSize
          UserStringHeapStart = heapSizes.UserStringHeapSize }

let private byteArrayComparer : IEqualityComparer<byte[]> =
    { new IEqualityComparer<byte[]> with
        member _.Equals(x, y) =
            if obj.ReferenceEquals(x, y) then true
            elif isNull (box x) || isNull (box y) then false
            elif x.Length <> y.Length then false
            else
                let mutable idx = 0
                let mutable equal = true
                while equal && idx < x.Length do
                    if x[idx] <> y[idx] then
                        equal <- false
                    idx <- idx + 1
                equal

        member _.GetHashCode(array: byte[]) =
            if isNull (box array) then 0
            else
                let mutable hash = 17
                for value in array do
                    hash <- (hash * 23) + int value
                hash }

let private writeCompressedUnsigned (writer: BinaryWriter) (value: int) =
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

type private RowTableBuilder() =
    let rows = ResizeArray<RowElementData[]>()

    member _.Add(elements: RowElementData[]) = rows.Add elements
    member _.Entries = rows.ToArray()
    member _.Count = rows.Count

type private StringHeapEntry =
    | New of string
    | Existing of int * string

type private StringHeapBuilder(baselineLength: int) =
    let entries = ResizeArray<StringHeapEntry>()
    let lookup = Dictionary<string, int>(StringComparer.Ordinal)
    let existingLookup = Dictionary<int, int>()
    let utf8 = Encoding.UTF8
    let mutable bytesCache: byte[] option = None
    let mutable offsetsCache: int[] option = None
    let mutable prefixBuffer : byte[] option =
        if baselineLength > 0 then
            let buffer = Array.zeroCreate<byte> baselineLength
            if buffer.Length > 0 then
                buffer[0] <- 0uy
            Some buffer
        else
            None

    let ensurePrefix lengthNeeded =
        match prefixBuffer with
        | Some buffer when buffer.Length >= lengthNeeded -> buffer
        | Some buffer ->
            let resized = Array.zeroCreate<byte> lengthNeeded
            Buffer.BlockCopy(buffer, 0, resized, 0, buffer.Length)
            prefixBuffer <- Some resized
            resized
        | None ->
            let length = max lengthNeeded 1
            let buffer = Array.zeroCreate<byte> length
            buffer[0] <- 0uy
            prefixBuffer <- Some buffer
            buffer

    member _.AddSharedEntry(value: string) : int =
        if String.IsNullOrEmpty value then
            0
        else
            match lookup.TryGetValue value with
            | true, index -> index
            | _ ->
                let index = entries.Count + 1
                entries.Add(StringHeapEntry.New value)
                lookup[value] <- index
                bytesCache <- None
                offsetsCache <- None
                index

    member _.AddExistingEntry(offset: int, value: string) : int =
        match existingLookup.TryGetValue offset with
        | true, index -> index
        | _ ->
            let index = entries.Count + 1
            entries.Add(StringHeapEntry.Existing(offset, value))
            existingLookup[offset] <- index
            bytesCache <- None
            offsetsCache <- None
            index

    member private this.BuildIfNeeded() =
        match bytesCache, offsetsCache with
        | Some _, Some _ -> ()
        | _ ->
            // Ensure prefix buffer carries all reused entries before writing.
            for entry in entries do
                match entry with
                | StringHeapEntry.Existing(offset, value) ->
                    let bytes = utf8.GetBytes value
                    let neededLength = offset + bytes.Length + 1
                    let prefix = ensurePrefix neededLength
                    Buffer.BlockCopy(bytes, 0, prefix, offset, bytes.Length)
                    prefix[offset + bytes.Length] <- 0uy
                | _ -> ()

            use ms = new MemoryStream()
            use writer = new BinaryWriter(ms, utf8, leaveOpen = true)
            let entryOffsets = Array.zeroCreate (entries.Count + 1)
            match prefixBuffer with
            | Some prefix -> writer.Write(prefix)
            | None -> writer.Write(byte 0)
            let mutable currentOffset = int ms.Length
            for i = 0 to entries.Count - 1 do
                let entryIndex = i + 1
                match entries.[i] with
                | StringHeapEntry.Existing(offset, _) ->
                    entryOffsets.[entryIndex] <- offset
                | StringHeapEntry.New value ->
                    entryOffsets.[entryIndex] <- currentOffset
                    let bytes = utf8.GetBytes value
                    writer.Write(bytes)
                    writer.Write(byte 0)
                    currentOffset <- currentOffset + bytes.Length + 1
            writer.Flush()
            bytesCache <- Some(ms.ToArray())
            offsetsCache <- Some entryOffsets

    member this.Bytes
        with get () =
            this.BuildIfNeeded()
            bytesCache.Value

    member this.EntryOffsets
        with get () =
            this.BuildIfNeeded()
            offsetsCache.Value

type private BlobHeapEntry =
    | New of byte[]
    | Existing of int * byte[]

type private ByteArrayHeapBuilder(baselineLength: int) =
    let entries = ResizeArray<BlobHeapEntry>()
    let lookup = Dictionary<byte[], int>(byteArrayComparer)
    let existingLookup = Dictionary<int, int>()
    let mutable bytesCache: byte[] option = None
    let mutable offsetsCache: int[] option = None
    let mutable prefixBuffer : byte[] option =
        if baselineLength > 0 then Some(Array.zeroCreate<byte> baselineLength) else None

    let encodeCompressedUnsigned value =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        writeCompressedUnsigned writer value
        writer.Flush()
        ms.ToArray()

    let ensurePrefix lengthNeeded =
        match prefixBuffer with
        | Some buffer when buffer.Length >= lengthNeeded -> buffer
        | Some buffer ->
            let resized = Array.zeroCreate<byte> lengthNeeded
            Buffer.BlockCopy(buffer, 0, resized, 0, buffer.Length)
            prefixBuffer <- Some resized
            resized
        | None ->
            let length = max lengthNeeded 1
            let buffer = Array.zeroCreate<byte> length
            prefixBuffer <- Some buffer
            buffer

    member _.AddSharedEntry(value: byte[]) : int =
        if isNull (box value) || value.Length = 0 then
            0
        else
            match lookup.TryGetValue value with
            | true, index -> index
            | _ ->
                let index = entries.Count + 1
                entries.Add(BlobHeapEntry.New value)
                lookup[value] <- index
                bytesCache <- None
                offsetsCache <- None
                index

    member _.AddExistingEntry(offset: int, value: byte[]) : int =
        match existingLookup.TryGetValue offset with
        | true, index -> index
        | _ ->
            let index = entries.Count + 1
            entries.Add(BlobHeapEntry.Existing(offset, value))
            existingLookup[offset] <- index
            bytesCache <- None
            offsetsCache <- None
            index

    member private this.BuildIfNeeded() =
        match bytesCache, offsetsCache with
        | Some _, Some _ -> ()
        | _ ->
            for entry in entries do
                match entry with
                | BlobHeapEntry.Existing(offset, value) ->
                    let encodedLength = encodeCompressedUnsigned value.Length
                    let neededLength = offset + encodedLength.Length + value.Length
                    let prefix = ensurePrefix neededLength
                    Buffer.BlockCopy(encodedLength, 0, prefix, offset, encodedLength.Length)
                    if value.Length > 0 then
                        Buffer.BlockCopy(value, 0, prefix, offset + encodedLength.Length, value.Length)
                | _ -> ()

            use ms = new MemoryStream()
            use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
            let entryOffsets = Array.zeroCreate (entries.Count + 1)
            match prefixBuffer with
            | Some prefix when prefix.Length > 0 -> writer.Write(prefix)
            | _ -> writer.Write(byte 0)
            let mutable currentOffset = int ms.Length
            for i = 0 to entries.Count - 1 do
                let entryIndex = i + 1
                match entries.[i] with
                | BlobHeapEntry.Existing(offset, _) ->
                    entryOffsets.[entryIndex] <- offset
                | BlobHeapEntry.New value ->
                    entryOffsets.[entryIndex] <- currentOffset
                    writeCompressedUnsigned writer value.Length
                    if value.Length > 0 then
                        writer.Write(value)
                    currentOffset <- int ms.Length
            writer.Flush()
            bytesCache <- Some(ms.ToArray())
            offsetsCache <- Some entryOffsets

    member this.Bytes
        with get () =
            this.BuildIfNeeded()
            bytesCache.Value

    member this.EntryOffsets
        with get () =
            this.BuildIfNeeded()
            offsetsCache.Value

    member _.Entries =
        entries
        |> Seq.choose (function
            | BlobHeapEntry.New value -> Some value
            | _ -> None)
        |> Seq.toArray

type DeltaMetadataTables(?heapOffsets: MetadataHeapOffsets) =
    let heapOffsets = defaultArg heapOffsets MetadataHeapOffsets.Zero
    let strings = StringHeapBuilder(heapOffsets.StringHeapStart)
    let blobs = ByteArrayHeapBuilder(heapOffsets.BlobHeapStart)
    let guids = ByteArrayHeapBuilder(heapOffsets.GuidHeapStart)
    let mutable stringHeapBytesCache: byte[] option = None
    let mutable blobHeapBytesCache: byte[] option = None
    let mutable guidHeapBytesCache: byte[] option = None

    let moduleRows = RowTableBuilder()
    let methodRows = RowTableBuilder()
    let paramRows = RowTableBuilder()
    let propertyRows = RowTableBuilder()
    let eventRows = RowTableBuilder()
    let propertyMapRows = RowTableBuilder()
    let eventMapRows = RowTableBuilder()
    let methodSemanticsRows = RowTableBuilder()
    let encLogRows = RowTableBuilder()
    let encMapRows = RowTableBuilder()

    let rowElement tag value =
        { Tag = tag
          Value = value }

    let rowElementUShort (value: uint16) = rowElement RowElementTags.UShort (int value)
    let rowElementULong (value: int) = rowElement RowElementTags.ULong value
    let rowElementString value = rowElement RowElementTags.String value
    let rowElementBlob value = rowElement RowElementTags.Blob value
    let rowElementGuid value = rowElement RowElementTags.Guid value
    let rowElementSimpleIndex table value = rowElement (RowElementTags.SimpleIndex table) value
    let rowElementTypeDefOrRef tag value = rowElement (RowElementTags.TypeDefOrRefOrSpec tag) value
    let rowElementHasSemantics tag value = rowElement (RowElementTags.HasSemantics tag) value

    let addStringValue (value: string) =
        if String.IsNullOrEmpty value then 0 else strings.AddSharedEntry value

    let addExistingStringHandle (handle: StringHandle) (value: string) =
        if handle.IsNil then
            0
        else
            strings.AddExistingEntry(MetadataTokens.GetHeapOffset handle, value)

    let addStringOption (value: string option) =
        match value with
        | Some v when not (String.IsNullOrEmpty v) -> strings.AddSharedEntry v
        | _ -> 0

    let addBlobBytes (bytes: byte[]) =
        if obj.ReferenceEquals(bytes, null) || bytes.Length = 0 then 0 else blobs.AddSharedEntry bytes

    let addExistingBlobHandle (handle: BlobHandle) (value: byte[]) =
        if handle.IsNil then
            0
        else
            blobs.AddExistingEntry(MetadataTokens.GetHeapOffset handle, value)

    let addGuidValue (value: Guid) =
        if value = System.Guid.Empty then 0 else guids.AddSharedEntry(value.ToByteArray())

    let encodeTypeDefOrRef (handle: EntityHandle) =
        if handle.IsNil then
            tdor_TypeDef, 0
        else
            let baseHandle = EntityHandle.op_Implicit handle
            match handle.Kind with
            | HandleKind.TypeDefinition -> tdor_TypeDef, MetadataTokens.GetRowNumber(TypeDefinitionHandle.op_Explicit baseHandle)
            | HandleKind.TypeReference -> tdor_TypeRef, MetadataTokens.GetRowNumber(TypeReferenceHandle.op_Explicit baseHandle)
            | HandleKind.TypeSpecification -> tdor_TypeSpec, MetadataTokens.GetRowNumber(TypeSpecificationHandle.op_Explicit baseHandle)
            | _ -> tdor_TypeDef, 0

    let buildStringHeapBytes () = strings.Bytes

    let buildBlobHeapBytes () = blobs.Bytes

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

    member _.AddModuleRow(name: string, moduleId: Guid, encId: Guid, encBaseId: Guid) =
        if moduleRows.Count = 0 then
            let row =
                [|
                    rowElementUShort 0us
                    rowElementString (addStringValue name)
                    rowElementGuid (addGuidValue moduleId)
                    rowElementGuid (addGuidValue encId)
                    rowElementGuid (addGuidValue encBaseId)
                |]
            moduleRows.Add row

    member _.AddMethodRow(row: MethodDefinitionRowInfo, body: MethodBodyUpdate) =
        let nameToken =
            match row.NameHandle with
            | Some handle when not row.IsAdded -> addExistingStringHandle handle row.Name
            | _ -> addStringValue row.Name

        let signatureToken =
            match row.SignatureHandle with
            | Some handle when not row.IsAdded -> addExistingBlobHandle handle row.Signature
            | _ -> addBlobBytes row.Signature

        let rowElements =
            [|
                rowElementULong body.CodeOffset
                rowElementUShort (uint16 row.ImplAttributes)
                rowElementUShort (uint16 row.Attributes)
                rowElementString nameToken
                rowElementBlob signatureToken
                rowElementSimpleIndex TableNames.Param (row.FirstParameterRowId |> Option.defaultValue 0)
            |]
        methodRows.Add rowElements

    member _.AddParameterRow(row: ParameterDefinitionRowInfo) =
        let nameIdx =
            match row.NameHandle with
            | Some handle when not row.IsAdded ->
                let value = row.Name |> Option.defaultValue String.Empty
                addExistingStringHandle handle value
            | _ -> addStringOption row.Name
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementUShort (uint16 row.SequenceNumber)
                rowElementString nameIdx
            |]
        paramRows.Add rowElements

    member _.AddPropertyRow(row: PropertyDefinitionRowInfo) =
        let nameToken =
            match row.NameHandle with
            | Some handle when not row.IsAdded -> addExistingStringHandle handle row.Name
            | _ -> addStringValue row.Name

        let signatureToken =
            match row.SignatureHandle with
            | Some handle when not row.IsAdded -> addExistingBlobHandle handle row.Signature
            | _ -> addBlobBytes row.Signature

        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementString nameToken
                rowElementBlob signatureToken
            |]
        propertyRows.Add rowElements

    member _.AddEventRow(row: EventDefinitionRowInfo) =
        let tdorTag, tdorRow = encodeTypeDefOrRef row.EventType
        let nameToken =
            match row.NameHandle with
            | Some handle when not row.IsAdded -> addExistingStringHandle handle row.Name
            | _ -> addStringValue row.Name
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementString nameToken
                rowElementTypeDefOrRef tdorTag tdorRow
            |]
        eventRows.Add rowElements

    member _.AddPropertyMapRow(row: PropertyMapRowInfo) =
        let rowElements =
            [|
                rowElementSimpleIndex TableNames.TypeDef row.TypeDefRowId
                rowElementSimpleIndex TableNames.Property (row.FirstPropertyRowId |> Option.defaultValue 0)
            |]
        propertyMapRows.Add rowElements

    member _.AddEventMapRow(row: EventMapRowInfo) =
        let rowElements =
            [|
                rowElementSimpleIndex TableNames.TypeDef row.TypeDefRowId
                rowElementSimpleIndex TableNames.Event (row.FirstEventRowId |> Option.defaultValue 0)
            |]
        eventMapRows.Add rowElements

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
                    let assocHandle = PropertyDefinitionHandle.op_Explicit(EntityHandle.op_Implicit row.Association)
                    hs_Property, MetadataTokens.GetRowNumber assocHandle
                | HandleKind.EventDefinition ->
                    let assocHandle = EventDefinitionHandle.op_Explicit(EntityHandle.op_Implicit row.Association)
                    hs_Event, MetadataTokens.GetRowNumber assocHandle
                | _ -> hs_Property, 0
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementSimpleIndex TableNames.Method methodRowId
                rowElementHasSemantics assocTag assocRowId
            |]
        methodSemanticsRows.Add rowElements

    member _.AddEncLogRow(tableIndex: TableIndex, rowId: int, operation: EditAndContinueOperation) =
        let entityHandle = MetadataTokens.EntityHandle(tableIndex, rowId)
        let token = MetadataTokens.GetToken(entityHandle)
        let rowElements =
            [|
                rowElementULong token
                rowElementULong (int operation)
            |]
        encLogRows.Add rowElements

    member _.AddEncMapRow(tableIndex: TableIndex, rowId: int) =
        let entityHandle = MetadataTokens.EntityHandle(tableIndex, rowId)
        let token = MetadataTokens.GetToken(entityHandle)
        let rowElements =
            [|
                rowElementULong token
            |]
        encMapRows.Add rowElements

    member _.StringHeapBytes
        with get () =
            match stringHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildStringHeapBytes ()
                stringHeapBytesCache <- Some bytes
                bytes

    member _.StringHeapOffsets = strings.EntryOffsets

    member _.BlobHeapBytes
        with get () =
            match blobHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildBlobHeapBytes ()
                blobHeapBytesCache <- Some bytes
                bytes

    member _.BlobHeapOffsets = blobs.EntryOffsets

    member _.GuidHeapBytes
        with get () =
            match guidHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildGuidHeapBytes ()
                guidHeapBytesCache <- Some bytes
                bytes

    member this.StringHeapSize = this.StringHeapBytes.Length

    member this.BlobHeapSize = this.BlobHeapBytes.Length

    member this.GuidHeapSize = this.GuidHeapBytes.Length

    member this.HeapSizes : MetadataHeapSizes =
        { StringHeapSize = this.StringHeapSize
          UserStringHeapSize = 0
          BlobHeapSize = this.BlobHeapSize
          GuidHeapSize = this.GuidHeapSize }

    member _.TableRows : TableRows =
        { Module = moduleRows.Entries
          MethodDef = methodRows.Entries
          Param = paramRows.Entries
          Property = propertyRows.Entries
          Event = eventRows.Entries
          PropertyMap = propertyMapRows.Entries
          EventMap = eventMapRows.Entries
          MethodSemantics = methodSemanticsRows.Entries
          EncLog = encLogRows.Entries
          EncMap = encMapRows.Entries }

    member _.HeapOffsets = heapOffsets

    member _.TableRowCounts : int[] =
        let counts = Array.zeroCreate MetadataTokens.TableCount
        counts[int TableIndex.Module] <- moduleRows.Count
        counts[int TableIndex.MethodDef] <- methodRows.Count
        counts[int TableIndex.Param] <- paramRows.Count
        counts[int TableIndex.Property] <- propertyRows.Count
        counts[int TableIndex.Event] <- eventRows.Count
        counts[int TableIndex.PropertyMap] <- propertyMapRows.Count
        counts[int TableIndex.EventMap] <- eventMapRows.Count
        counts[int TableIndex.MethodSemantics] <- methodSemanticsRows.Count
        counts[int TableIndex.EncLog] <- encLogRows.Count
        counts[int TableIndex.EncMap] <- encMapRows.Count
        counts
