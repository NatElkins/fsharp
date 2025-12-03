// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// The IL Binary writer.
module internal FSharp.Compiler.AbstractIL.ILBinaryWriter

open System.Collections.Generic
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.AbstractIL.StrongNameSign
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.AbstractIL.ILMetadataHeaps
open FSharp.Compiler.AbstractIL.ILEncLogWriter

module internal RowElementTags =
    [<Literal>] val UShort: int = 0
    [<Literal>] val ULong: int = 1
    [<Literal>] val Data: int = 2
    [<Literal>] val DataResources: int = 3
    [<Literal>] val Guid: int = 4
    [<Literal>] val Blob: int = 5
    [<Literal>] val String: int = 6
    [<Literal>] val SimpleIndexMin: int = 7
    [<Literal>] val SimpleIndexMax: int = 119
    val SimpleIndex: table: TableName -> int
    [<Literal>] val TypeDefOrRefOrSpecMin: int = 120
    [<Literal>] val TypeDefOrRefOrSpecMax: int = 122
    val TypeDefOrRefOrSpec: tag: TypeDefOrRefTag -> int
    [<Literal>] val TypeOrMethodDefMin: int = 123
    [<Literal>] val TypeOrMethodDefMax: int = 124
    val TypeOrMethodDef: tag: TypeOrMethodDefTag -> int
    [<Literal>] val HasConstantMin: int = 125
    [<Literal>] val HasConstantMax: int = 127
    val HasConstant: tag: HasConstantTag -> int
    [<Literal>] val HasCustomAttributeMin: int = 128
    [<Literal>] val HasCustomAttributeMax: int = 149
    val HasCustomAttribute: tag: HasCustomAttributeTag -> int
    [<Literal>] val HasFieldMarshalMin: int = 150
    [<Literal>] val HasFieldMarshalMax: int = 151
    val HasFieldMarshal: tag: HasFieldMarshalTag -> int
    [<Literal>] val HasDeclSecurityMin: int = 152
    [<Literal>] val HasDeclSecurityMax: int = 154
    val HasDeclSecurity: tag: HasDeclSecurityTag -> int
    [<Literal>] val MemberRefParentMin: int = 155
    [<Literal>] val MemberRefParentMax: int = 159
    val MemberRefParent: tag: MemberRefParentTag -> int
    [<Literal>] val HasSemanticsMin: int = 160
    [<Literal>] val HasSemanticsMax: int = 161
    val HasSemantics: tag: HasSemanticsTag -> int
    [<Literal>] val MethodDefOrRefMin: int = 162
    [<Literal>] val MethodDefOrRefMax: int = 164
    val MethodDefOrRef: tag: MethodDefOrRefTag -> int
    [<Literal>] val MemberForwardedMin: int = 165
    [<Literal>] val MemberForwardedMax: int = 166
    val MemberForwarded: tag: MemberForwardedTag -> int
    [<Literal>] val ImplementationMin: int = 167
    [<Literal>] val ImplementationMax: int = 169
    val Implementation: tag: ImplementationTag -> int
    [<Literal>] val CustomAttributeTypeMin: int = 170
    [<Literal>] val CustomAttributeTypeMax: int = 173
    val CustomAttributeType: tag: CustomAttributeTypeTag -> int
    [<Literal>] val ResolutionScopeMin: int = 174
    [<Literal>] val ResolutionScopeMax: int = 178
    val ResolutionScope: tag: ResolutionScopeTag -> int

[<Struct>]
type RowElement =
    new: int * int -> RowElement
    member Tag: int
    member Val: int

val UShort: uint16 -> RowElement
val ULong: int -> RowElement
val Guid: int -> RowElement
val Blob: int -> RowElement
val StringE: int -> RowElement
val SimpleIndex: table: TableName * index: int -> RowElement
val TypeDefOrRefOrSpec: tag: TypeDefOrRefTag * index: int -> RowElement
val HasSemantics: tag: HasSemanticsTag * index: int -> RowElement

/// Computes the trailing byte for a user string blob per ECMA-335 II.24.2.4.
/// Returns 1 if any character needs special handling, 0 otherwise.
val markerForUnicodeBytes: b: byte[] -> int

[<Struct; CustomEquality; NoComparison>]
type UnsharedRow =
    new: RowElement[] -> UnsharedRow
    member GenericRow: RowElement[]

[<Sealed>]
type MetadataTable<'T when 'T : not null> =
    member Count: int
    static member New: string * IEqualityComparer<'T> -> MetadataTable<'T> when 'T : not null
    member Entries: 'T list
    member EntriesAsArray: 'T[]
    member AddSharedEntry: 'T -> int
    member AddUnsharedEntry: 'T -> int
    member FindOrAddSharedEntry: 'T -> int
    member Contains: 'T -> bool
    member SetRowsOfTable: 'T[] -> unit
    member AddUniqueEntry: string -> ('T -> string) -> 'T -> int
    member GetTableEntry: 'T -> int

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

/// Creates an IEncLogWriter for full assembly emission (no-op).
/// Delta emission uses a different implementation that records entries.
val createNullEncLogWriter: unit -> IEncLogWriter
