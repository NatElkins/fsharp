// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// The IL Binary writer.
module internal FSharp.Compiler.AbstractIL.ILBinaryWriter

open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.AbstractIL.StrongNameSign

type options =
    { ilg: ILGlobals
      outfile: string
      pdbfile: string option
      portablePDB: bool
      embeddedPDB: bool
      embedAllSource: bool
      embedSourceList: string list
      allGivenSources: ILSourceDocument list
      sourceLink: string
      checksumAlgorithm: HashAlgorithm
      signer: ILStrongNameSigner option
      emitTailcalls: bool
      deterministic: bool
      dumpDebugInfo: bool
      referenceAssemblyOnly: bool
      referenceAssemblyAttribOpt: ILAttribute option
      referenceAssemblySignatureHash: int option
      pathMap: PathMap }

/// <summary>
/// Captures the various metadata token mapping functions produced by the IL writer.
/// </summary>
[<NoEquality; NoComparison>]
type ILTokenMappings =
    { TypeDefTokenMap: ILTypeDef list * ILTypeDef -> int32
      FieldDefTokenMap: ILTypeDef list * ILTypeDef -> ILFieldDef -> int32
      MethodDefTokenMap: ILTypeDef list * ILTypeDef -> ILMethodDef -> int32
      PropertyTokenMap: ILTypeDef list * ILTypeDef -> ILPropertyDef -> int32
      EventTokenMap: ILTypeDef list * ILTypeDef -> ILEventDef -> int32 }

/// <summary>
/// Records the uncompressed heap sizes produced during metadata emission so that later delta passes
/// can reason about stream growth.
/// </summary>
[<NoEquality; NoComparison>]
type MetadataHeapSizes =
    { StringHeapSize: int
      UserStringHeapSize: int
      BlobHeapSize: int
      GuidHeapSize: int }

/// <summary>
/// Snapshot of the emitted metadata state that is required to seed hot reload baseline calculations.
/// </summary>
[<NoEquality; NoComparison>]
type MetadataSnapshot =
    { HeapSizes: MetadataHeapSizes
      TableRowCounts: int[]
      GuidHeapStart: int }

/// Write a binary to the file system.
val WriteILBinaryFile: options: options * inputModule: ILModuleDef * (ILAssemblyRef -> ILAssemblyRef) -> unit

/// Write a binary to an array of bytes suitable for dynamic loading.
val WriteILBinaryInMemory:
    options: options * inputModule: ILModuleDef * (ILAssemblyRef -> ILAssemblyRef) -> byte[] * byte[] option

/// Write a binary to an array of bytes and capture token and metadata artifacts.
val WriteILBinaryInMemoryWithArtifacts:
    options: options *
    inputModule: ILModuleDef *
    (ILAssemblyRef -> ILAssemblyRef) ->
        byte[] * byte[] option * ILTokenMappings * MetadataSnapshot
