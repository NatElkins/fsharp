module internal FSharp.Compiler.CodeGen.DeltaMetadataSerializer

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaMetadataTypes
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.CodeGen.DeltaIndexSizing

let private padTo4 (bytes: byte[]) =
    if bytes.Length % 4 = 0 then
        bytes
    else
        let padded = Array.zeroCreate<byte> (bytes.Length + (4 - (bytes.Length % 4)))
        Array.Copy(bytes, padded, bytes.Length)
        padded

let private emptyUserStringHeap = padTo4 [| 0uy |]

/// Represents the aligned heap streams that will be written into the delta metadata.
type DeltaHeapStreams =
    { Strings: byte[]
      StringsLength: int
      Blobs: byte[]
      BlobsLength: int
      Guids: byte[]
      GuidsLength: int
      UserStrings: byte[]
      UserStringsLength: int }

    static member Empty =
        { Strings = padTo4 [||]
          StringsLength = 0
          Blobs = padTo4 [||]
          BlobsLength = 0
          Guids = padTo4 [||]
          GuidsLength = 0
          UserStrings = emptyUserStringHeap
          UserStringsLength = 1 }

let buildHeapStreams (mirror: DeltaMetadataTables) : DeltaHeapStreams =
    let stringBytes = mirror.StringHeapBytes
    let blobBytes = mirror.BlobHeapBytes
    let guidBytes = mirror.GuidHeapBytes
    let userStringBytes = mirror.UserStringHeapBytes

    { Strings = padTo4 stringBytes
      StringsLength = stringBytes.Length
      Blobs = padTo4 blobBytes
      BlobsLength = blobBytes.Length
      Guids = padTo4 guidBytes
      GuidsLength = guidBytes.Length
      UserStrings = padTo4 userStringBytes
      UserStringsLength = userStringBytes.Length }

/// Represents the serialized `#~` stream (metadata tables) including its padded bytes.
type DeltaTableStream =
    { Bytes: byte[]
      UnpaddedSize: int
      PaddedSize: int }

/// Captures the sizing data needed to build delta metadata, mirroring Roslyn's MetadataSizes.
type DeltaMetadataSizes =
    { RowCounts: int[]
      HeapSizes: MetadataHeapSizes
      BitMasks: TableBitMasks
      IndexSizes: CodedIndexSizes
      IsEncDelta: bool }

let private promoteIndicesForEncDelta (sizes: CodedIndexSizes) : CodedIndexSizes =
    let simpleIndexBig = Array.create MetadataTokens.TableCount true
    { sizes with
        StringsBig = true
        GuidsBig = true
        BlobsBig = true
        SimpleIndexBig = simpleIndexBig
        TypeDefOrRefBig = true
        TypeOrMethodDefBig = true
        HasConstantBig = true
        HasCustomAttributeBig = true
        HasFieldMarshalBig = true
        HasDeclSecurityBig = true
        MemberRefParentBig = true
        HasSemanticsBig = true
        MethodDefOrRefBig = true
        MemberForwardedBig = true
        ImplementationBig = true
        CustomAttributeTypeBig = true
        ResolutionScopeBig = true }

let computeMetadataSizes (tableMirror: DeltaMetadataTables) : DeltaMetadataSizes =
    let rowCounts = tableMirror.TableRowCounts
    let heapSizes = tableMirror.HeapSizes
    let bitMasks = DeltaTableLayout.computeBitMasks rowCounts
    let isEncDelta =
        rowCounts[int TableIndex.EncLog] > 0
        || rowCounts[int TableIndex.EncMap] > 0
    let indexSizes =
        DeltaIndexSizing.compute rowCounts heapSizes
        |> fun sizes -> if isEncDelta then promoteIndicesForEncDelta sizes else sizes

    { RowCounts = rowCounts
      HeapSizes = heapSizes
      BitMasks = bitMasks
      IndexSizes = indexSizes
      IsEncDelta = isEncDelta }

type DeltaTableSerializerInput =
    { Tables: TableRows
      MetadataSizes: DeltaMetadataSizes
      StringHeap: byte[]
      StringHeapOffsets: int[]
      BlobHeap: byte[]
      BlobHeapOffsets: int[]
      GuidHeap: byte[] }

let private writeUInt16 (writer: BinaryWriter) (value: int) =
    writer.Write(uint16 value)

let private writeUInt32 (writer: BinaryWriter) (value: int) =
    writer.Write(value)

let private writeHeapIndex (writer: BinaryWriter) (isBig: bool) (value: int) =
    if isBig then writeUInt32 writer value else writeUInt16 writer value

let private writeTaggedIndex (writer: BinaryWriter) (nbits: int) (isBig: bool) (tag: int) (value: int) =
    let encoded = (value <<< nbits) ||| tag
    if isBig then writeUInt32 writer encoded else writeUInt16 writer encoded

let private tableRowsByIndex (tables: TableRows) =
    let rows = Array.create MetadataTokens.TableCount Array.empty
    rows[int TableIndex.Module] <- tables.Module
    rows[int TableIndex.MethodDef] <- tables.MethodDef
    rows[int TableIndex.Param] <- tables.Param
    rows[int TableIndex.Property] <- tables.Property
    rows[int TableIndex.Event] <- tables.Event
    rows[int TableIndex.PropertyMap] <- tables.PropertyMap
    rows[int TableIndex.EventMap] <- tables.EventMap
    rows[int TableIndex.MethodSemantics] <- tables.MethodSemantics
    rows[int TableIndex.EncLog] <- tables.EncLog
    rows[int TableIndex.EncMap] <- tables.EncMap
    rows

let private isTablePresent (bitmaskLow: int) (bitmaskHigh: int) (index: int) =
    if index < 32 then
        ((bitmaskLow >>> index) &&& 1) <> 0
    else
        ((bitmaskHigh >>> (index - 32)) &&& 1) <> 0

let private writeRowElement (writer: BinaryWriter) (indexSizes: CodedIndexSizes) (input: DeltaTableSerializerInput) (element: RowElementData) =
    let tag = element.Tag
    let value = element.Value

    if tag = RowElementTags.UShort then
        writeUInt16 writer value
    elif tag = RowElementTags.ULong then
        writeUInt32 writer value
    elif tag = RowElementTags.String then
        let offset = if value = 0 then 0 else input.StringHeapOffsets.[value]
        writeHeapIndex writer indexSizes.StringsBig offset
    elif tag = RowElementTags.Blob then
        let offset = if value = 0 then 0 else input.BlobHeapOffsets.[value]
        writeHeapIndex writer indexSizes.BlobsBig offset
    elif tag = RowElementTags.Guid then
        writeHeapIndex writer indexSizes.GuidsBig value
    elif tag >= RowElementTags.SimpleIndexMin && tag <= RowElementTags.SimpleIndexMax then
        let tableIndex = tag - RowElementTags.SimpleIndexMin
        writeHeapIndex writer indexSizes.SimpleIndexBig.[tableIndex] value
    elif tag >= RowElementTags.TypeDefOrRefOrSpecMin && tag <= RowElementTags.TypeDefOrRefOrSpecMax then
        let subTag = tag - RowElementTags.TypeDefOrRefOrSpecMin
        writeTaggedIndex writer 2 indexSizes.TypeDefOrRefBig subTag value
    elif tag >= RowElementTags.TypeOrMethodDefMin && tag <= RowElementTags.TypeOrMethodDefMax then
        let subTag = tag - RowElementTags.TypeOrMethodDefMin
        writeTaggedIndex writer 1 indexSizes.TypeOrMethodDefBig subTag value
    elif tag >= RowElementTags.HasConstantMin && tag <= RowElementTags.HasConstantMax then
        let subTag = tag - RowElementTags.HasConstantMin
        writeTaggedIndex writer 2 indexSizes.HasConstantBig subTag value
    elif tag >= RowElementTags.HasCustomAttributeMin && tag <= RowElementTags.HasCustomAttributeMax then
        let subTag = tag - RowElementTags.HasCustomAttributeMin
        writeTaggedIndex writer 5 indexSizes.HasCustomAttributeBig subTag value
    elif tag >= RowElementTags.HasFieldMarshalMin && tag <= RowElementTags.HasFieldMarshalMax then
        let subTag = tag - RowElementTags.HasFieldMarshalMin
        writeTaggedIndex writer 1 indexSizes.HasFieldMarshalBig subTag value
    elif tag >= RowElementTags.HasDeclSecurityMin && tag <= RowElementTags.HasDeclSecurityMax then
        let subTag = tag - RowElementTags.HasDeclSecurityMin
        writeTaggedIndex writer 2 indexSizes.HasDeclSecurityBig subTag value
    elif tag >= RowElementTags.MemberRefParentMin && tag <= RowElementTags.MemberRefParentMax then
        let subTag = tag - RowElementTags.MemberRefParentMin
        writeTaggedIndex writer 3 indexSizes.MemberRefParentBig subTag value
    elif tag >= RowElementTags.HasSemanticsMin && tag <= RowElementTags.HasSemanticsMax then
        let subTag = tag - RowElementTags.HasSemanticsMin
        writeTaggedIndex writer 1 indexSizes.HasSemanticsBig subTag value
    elif tag >= RowElementTags.MethodDefOrRefMin && tag <= RowElementTags.MethodDefOrRefMax then
        let subTag = tag - RowElementTags.MethodDefOrRefMin
        writeTaggedIndex writer 1 indexSizes.MethodDefOrRefBig subTag value
    elif tag >= RowElementTags.MemberForwardedMin && tag <= RowElementTags.MemberForwardedMax then
        let subTag = tag - RowElementTags.MemberForwardedMin
        writeTaggedIndex writer 1 indexSizes.MemberForwardedBig subTag value
    elif tag >= RowElementTags.ImplementationMin && tag <= RowElementTags.ImplementationMax then
        let subTag = tag - RowElementTags.ImplementationMin
        writeTaggedIndex writer 2 indexSizes.ImplementationBig subTag value
    elif tag >= RowElementTags.CustomAttributeTypeMin && tag <= RowElementTags.CustomAttributeTypeMax then
        let subTag = tag - RowElementTags.CustomAttributeTypeMin
        writeTaggedIndex writer 3 indexSizes.CustomAttributeTypeBig subTag value
    elif tag >= RowElementTags.ResolutionScopeMin && tag <= RowElementTags.ResolutionScopeMax then
        let subTag = tag - RowElementTags.ResolutionScopeMin
        writeTaggedIndex writer 2 indexSizes.ResolutionScopeBig subTag value
    else
        failwithf "Unsupported row element tag: %d" tag

let private align4 value = (value + 3) &&& ~~~3

let buildTableStream (input: DeltaTableSerializerInput) : DeltaTableStream =
    let sizes = input.MetadataSizes
    let bitMasks = sizes.BitMasks
    let indexSizes = sizes.IndexSizes
    use ms = new MemoryStream()
    use writer = new BinaryWriter(ms)

    writer.Write(0u)
    writer.Write(byte 2)
    writer.Write(byte 0)

    let heapFlags =
        let baseFlags =
            (if indexSizes.StringsBig then 0x01 else 0)
            ||| (if indexSizes.GuidsBig then 0x02 else 0)
            ||| (if indexSizes.BlobsBig then 0x04 else 0)
        let encFlags = if sizes.IsEncDelta then (0x20 ||| 0x80) else 0
        baseFlags ||| encFlags

    writer.Write(byte heapFlags)
    writer.Write(byte 1)
    writer.Write(bitMasks.ValidLow)
    writer.Write(bitMasks.ValidHigh)
    writer.Write(bitMasks.SortedLow)
    writer.Write(bitMasks.SortedHigh)

    for tableIndex = 0 to MetadataTokens.TableCount - 1 do
        if isTablePresent bitMasks.ValidLow bitMasks.ValidHigh tableIndex then
            writer.Write(sizes.RowCounts.[tableIndex])

    let rowsByIndex = tableRowsByIndex input.Tables

    for tableIndex = 0 to MetadataTokens.TableCount - 1 do
        let rows = rowsByIndex.[tableIndex]
        if rows.Length > 0 then
            for row in rows do
                for element in row do
                    writeRowElement writer indexSizes input element

    writer.Flush()
    let unpaddedSize = int ms.Length
    let paddedSize = align4 unpaddedSize
    let bytes = ms.ToArray()
    if paddedSize = unpaddedSize then
        { Bytes = bytes
          UnpaddedSize = unpaddedSize
          PaddedSize = paddedSize }
    else
        let padded = Array.zeroCreate<byte> paddedSize
        Array.Copy(bytes, padded, bytes.Length)
        { Bytes = padded
          UnpaddedSize = unpaddedSize
          PaddedSize = paddedSize }

type private StreamDescriptor =
    { Name: string
      Offset: int
      Size: int
      Bytes: byte[] }

let private versionString = "v4.0.30319"

let private encodeName (writer: BinaryWriter) (name: string) =
    let bytes = Text.Encoding.UTF8.GetBytes(name)
    writer.Write(bytes)
    writer.Write(byte 0)
    while writer.BaseStream.Position % 4L <> 0L do
        writer.Write(byte 0)

let private streamHeaderSize (name: string) =
    let nameLength = Text.Encoding.UTF8.GetByteCount(name) + 1
    8 + align4 nameLength

let serializeMetadataRoot (_: DeltaTableSerializerInput) (heaps: DeltaHeapStreams) (tableStream: DeltaTableStream) : byte[] =
    let streams =
        [ "#-", tableStream.UnpaddedSize, tableStream.Bytes
          "#Strings", heaps.StringsLength, heaps.Strings
          "#US", heaps.UserStringsLength, heaps.UserStrings
          "#GUID", heaps.GuidsLength, heaps.Guids
          "#Blob", heaps.BlobsLength, heaps.Blobs
          "#JTD", 0, Array.empty ]

    let versionBytes = Text.Encoding.UTF8.GetBytes(versionString)
    let versionStringLength = versionBytes.Length + 1
    let versionLength = align4 versionStringLength

    let headerBaseSize = 4 + 2 + 2 + 4 + 4 + versionLength + 2 + 2
    let streamsHeaderSize = streams |> List.sumBy (fun (name, _, _) -> streamHeaderSize name)
    let headerSize = headerBaseSize + streamsHeaderSize

    let mutable offset = headerSize
    let descriptors =
        streams
        |> List.map (fun (name, size, bytes) ->
            let descriptor = { Name = name; Offset = offset; Size = size; Bytes = bytes }
            offset <- offset + bytes.Length
            descriptor)

    use ms = new MemoryStream()
    use writer = new BinaryWriter(ms)

    writer.Write(0x424A5342u)
    writer.Write(uint16 1)
    writer.Write(uint16 1)
    writer.Write(0u)
    writer.Write(uint32 versionLength)
    writer.Write(versionBytes)
    writer.Write(byte 0)
    let paddingBytes = versionLength - versionStringLength
    if paddingBytes > 0 then
        writer.Write(Array.zeroCreate<byte> paddingBytes)
    while ms.Position % 4L <> 0L do
        writer.Write(byte 0)

    writer.Write(uint16 0)
    writer.Write(uint16 descriptors.Length)

    for descriptor in descriptors do
        writer.Write(uint32 descriptor.Offset)
        writer.Write(uint32 descriptor.Size)
        encodeName writer descriptor.Name

    for descriptor in descriptors do
        writer.Write(descriptor.Bytes)

    ms.ToArray()
