module internal FSharp.Compiler.CodeGen.DeltaMetadataSerializer

open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.CodeGen.DeltaMetadataTables
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

let private buildHeapAddressTable (heapBytes: byte[]) =
    let table = Dictionary<int, int>()
    table[0] <- 0
    let mutable offset = 1
    let mutable token = 1
    for i = 0 to heapBytes.Length - 1 do
        if heapBytes.[i] = 0uy then
            table[token] <- offset
            token <- token + 1
        offset <- offset + 1
    table

type DeltaTableSerializerInput =
    { Tables: TableRows
      RowCounts: int[]
      BitMasks: TableBitMasks
      IndexSizes: CodedIndexSizes
      StringHeap: byte[]
      BlobHeap: byte[]
      GuidHeap: byte[] }

/// Placeholder until the AbstractIL serializer is fully implemented.
let buildTableStream (_input: DeltaTableSerializerInput) : DeltaTableStream =
    { Bytes = Array.empty
      UnpaddedSize = 0
      PaddedSize = 0 }
