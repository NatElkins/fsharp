module internal FSharp.Compiler.CodeGen.DeltaMetadataSerializer

open System
open System.Collections.Generic
open System.IO
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.CodeGen.DeltaIndexSizing
open FSharp.Compiler.AbstractIL.ILBinaryWriter

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
      Blobs: byte[]
      Guids: byte[]
      UserStrings: byte[] }

    static member Empty =
        { Strings = padTo4 [||]
          Blobs = padTo4 [||]
          Guids = padTo4 [||]
          UserStrings = emptyUserStringHeap }

let buildHeapStreams (mirror: DeltaMetadataTables) : DeltaHeapStreams =
    { Strings = padTo4 mirror.StringHeapBytes
      Blobs = padTo4 mirror.BlobHeapBytes
      Guids = padTo4 mirror.GuidHeapBytes
      UserStrings = emptyUserStringHeap }

/// Represents the serialized `#~` stream (metadata tables) including its padded bytes.
type DeltaTableStream =
    { Bytes: byte[]
      UnpaddedSize: int
      PaddedSize: int }

type DeltaTableSerializerInput =
    { Tables: TableRows
      RowCounts: int[]
      BitMasks: TableBitMasks
      IndexSizes: CodedIndexSizes
      StringHeap: byte[]
      BlobHeap: byte[]
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
    rows

let private isTablePresent (bitmaskLow: int) (bitmaskHigh: int) (index: int) =
    if index < 32 then
        ((bitmaskLow >>> index) &&& 1) <> 0
    else
        ((bitmaskHigh >>> (index - 32)) &&& 1) <> 0

let private writeRowElement (writer: BinaryWriter) (indexSizes: CodedIndexSizes) (element: RowElement) =
    let tag = element.Tag
    let value = element.Val

    if tag = RowElementTags.UShort then
        writeUInt16 writer value
    elif tag = RowElementTags.ULong then
        writeUInt32 writer value
    elif tag = RowElementTags.String then
        writeHeapIndex writer indexSizes.StringsBig value
    elif tag = RowElementTags.Blob then
        writeHeapIndex writer indexSizes.BlobsBig value
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

let private align4 value = (value + 3) &&& ~3

let buildTableStream (input: DeltaTableSerializerInput) : DeltaTableStream =
    use ms = new MemoryStream()
    use writer = new BinaryWriter(ms)

    writer.Write(0u) // Reserved
    writer.Write(uint16 2) // Major version
    writer.Write(uint16 0) // Minor version

    let heapFlags =
        (if input.IndexSizes.StringsBig then 0x01 else 0)
        ||| (if input.IndexSizes.GuidsBig then 0x02 else 0)
        ||| (if input.IndexSizes.BlobsBig then 0x04 else 0)

    writer.Write(byte heapFlags)
    writer.Write(byte 1) // reserved
    writer.Write(input.BitMasks.ValidLow)
    writer.Write(input.BitMasks.ValidHigh)
    writer.Write(input.BitMasks.SortedLow)
    writer.Write(input.BitMasks.SortedHigh)

    for tableIndex = 0 to MetadataTokens.TableCount - 1 do
        if isTablePresent input.BitMasks.ValidLow input.BitMasks.ValidHigh tableIndex then
            writer.Write(input.RowCounts.[tableIndex])

    let rowsByIndex = tableRowsByIndex input.Tables

    for tableIndex = 0 to MetadataTokens.TableCount - 1 do
        let rows = rowsByIndex.[tableIndex]
        if rows.Length > 0 then
            for row in rows do
                for element in row do
                    writeRowElement writer input.IndexSizes element

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
