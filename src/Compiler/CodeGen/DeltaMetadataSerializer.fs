module internal FSharp.Compiler.CodeGen.DeltaMetadataSerializer

open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.CodeGen.DeltaIndexSizing
open FSharp.Compiler.AbstractIL.ILBinaryWriter

type DeltaSerializationStrategy =
    | UseMetadataBuilder
    | UseAbstractIL

let private serializationStrategy () =
    match System.Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_USE_ABSTRACTIL") with
    | null -> UseMetadataBuilder
    | value when value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) -> UseAbstractIL
    | _ -> UseMetadataBuilder
@@
let buildTableStream (input: DeltaTableSerializerInput) : DeltaTableStream =
    use ms = new MemoryStream()
    use writer = new BinaryWriter(ms)
@@
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

let tryUseAbstractIlStream metadataDelta : DeltaTableStream option =
    match serializationStrategy () with
    | UseAbstractIL -> Some(buildTableStream metadataDelta)
    | UseMetadataBuilder -> None
