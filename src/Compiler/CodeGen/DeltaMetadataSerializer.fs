module internal FSharp.Compiler.CodeGen.DeltaMetadataSerializer

open System
open FSharp.Compiler.CodeGen.DeltaMetadataTables

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
