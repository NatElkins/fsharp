module internal FSharp.Compiler.CodeGen.DeltaIndexSizing

open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.ILDeltaHandles

type MetadataHeapSizes = FSharp.Compiler.AbstractIL.ILBinaryWriter.MetadataHeapSizes

type CodedIndexSizes =
    { StringsBig: bool
      GuidsBig: bool
      BlobsBig: bool
      SimpleIndexBig: bool[]
      TypeDefOrRefBig: bool
      TypeOrMethodDefBig: bool
      HasConstantBig: bool
      HasCustomAttributeBig: bool
      HasFieldMarshalBig: bool
      HasDeclSecurityBig: bool
      MemberRefParentBig: bool
      HasSemanticsBig: bool
      MethodDefOrRefBig: bool
      MemberForwardedBig: bool
      ImplementationBig: bool
      CustomAttributeTypeBig: bool
      ResolutionScopeBig: bool }

let private tableSize (tableRowCounts: int[]) (table: TableIndex) =
    tableRowCounts.[int table]

let private totalRowCount
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (table: TableIndex)
    =
    let index = int table
    let external =
        if externalRowCounts.Length = tableRowCounts.Length then
            externalRowCounts.[index]
        else
            0
    tableRowCounts.[index] + external

let private referenceExceedsLimit
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (maxValueExclusive: int)
    (tables: TableIndex[])
    =
    tables
    |> Array.exists (fun table ->
        totalRowCount tableRowCounts externalRowCounts table >= maxValueExclusive)

let private codedBigness
    (tagBits: int)
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (isCompressed: bool)
    (tables: TableIndex[])
    =
    if not isCompressed then
        true
    else
        let limit = pown 2 (16 - tagBits)
        referenceExceedsLimit tableRowCounts externalRowCounts limit tables

let private isSimpleIndexBig
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (isCompressed: bool)
    (tableIndex: int)
    =
    if not isCompressed then
        true
    else
        let local =
            if tableIndex < tableRowCounts.Length then tableRowCounts.[tableIndex] else 0
        let external =
            if tableIndex < externalRowCounts.Length then externalRowCounts.[tableIndex] else 0
        local + external >= 0x10000

let compute
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (heapSizes: MetadataHeapSizes)
    (isEncDelta: bool)
    : CodedIndexSizes =

    let isCompressed = not isEncDelta

    let stringsBig = (not isCompressed) || heapSizes.StringHeapSize >= 0x10000
    let blobsBig = (not isCompressed) || heapSizes.BlobHeapSize >= 0x10000
    let guidsBig = (not isCompressed) || heapSizes.GuidHeapSize >= 0x10000

    let simpleIndexBig =
        Array.init DeltaTokens.TableCount (fun i ->
            isSimpleIndexBig tableRowCounts externalRowCounts isCompressed i)

    let coded tag tables =
        codedBigness tag tableRowCounts externalRowCounts isCompressed tables

    let typeDefOrRefBig =
        coded 2
            [| TableIndex.TypeDef
               TableIndex.TypeRef
               TableIndex.TypeSpec |]

    let typeOrMethodDefBig =
        coded 1
            [| TableIndex.TypeDef
               TableIndex.MethodDef |]

    let hasConstantBig =
        coded 2
            [| TableIndex.Field
               TableIndex.Param
               TableIndex.Property |]

    let hasCustomAttributeBig =
        coded 5
            [| TableIndex.MethodDef
               TableIndex.Field
               TableIndex.TypeRef
               TableIndex.TypeDef
               TableIndex.Param
               TableIndex.InterfaceImpl
               TableIndex.MemberRef
               TableIndex.Module
               TableIndex.DeclSecurity
               TableIndex.Property
               TableIndex.Event
               TableIndex.StandAloneSig
               TableIndex.ModuleRef
               TableIndex.TypeSpec
               TableIndex.Assembly
               TableIndex.AssemblyRef
               TableIndex.File
               TableIndex.ExportedType
               TableIndex.ManifestResource
               TableIndex.GenericParam
               TableIndex.GenericParamConstraint
               TableIndex.MethodSpec |]

    let hasFieldMarshalBig =
        coded 1
            [| TableIndex.Field
               TableIndex.Param |]

    // ECMA-335 II.24.2.6: HasDeclSecurity - TypeDef(0), MethodDef(1), Assembly(2)
    let hasDeclSecurityBig =
        coded 2
            [| TableIndex.TypeDef
               TableIndex.MethodDef
               TableIndex.Assembly |]

    // ECMA-335 II.24.2.6: MemberRefParent - TypeDef(0), TypeRef(1), ModuleRef(2), MethodDef(3), TypeSpec(4)
    let memberRefParentBig =
        coded 3
            [| TableIndex.TypeDef
               TableIndex.TypeRef
               TableIndex.ModuleRef
               TableIndex.MethodDef
               TableIndex.TypeSpec |]

    let hasSemanticsBig =
        coded 1
            [| TableIndex.Event
               TableIndex.Property |]

    let methodDefOrRefBig =
        coded 1
            [| TableIndex.MethodDef
               TableIndex.MemberRef |]

    let memberForwardedBig =
        coded 1
            [| TableIndex.Field
               TableIndex.MethodDef |]

    let implementationBig =
        coded 2
            [| TableIndex.File
               TableIndex.AssemblyRef
               TableIndex.ExportedType |]

    let customAttributeTypeBig =
        coded 3
            [| TableIndex.MethodDef
               TableIndex.MemberRef |]

    let resolutionScopeBig =
        coded 2
            [| TableIndex.Module
               TableIndex.ModuleRef
               TableIndex.AssemblyRef
               TableIndex.TypeRef |]

    { StringsBig = stringsBig
      GuidsBig = guidsBig
      BlobsBig = blobsBig
      SimpleIndexBig = simpleIndexBig
      TypeDefOrRefBig = typeDefOrRefBig
      TypeOrMethodDefBig = typeOrMethodDefBig
      HasConstantBig = hasConstantBig
      HasCustomAttributeBig = hasCustomAttributeBig
      HasFieldMarshalBig = hasFieldMarshalBig
      HasDeclSecurityBig = hasDeclSecurityBig
      MemberRefParentBig = memberRefParentBig
      HasSemanticsBig = hasSemanticsBig
      MethodDefOrRefBig = methodDefOrRefBig
      MemberForwardedBig = memberForwardedBig
      ImplementationBig = implementationBig
      CustomAttributeTypeBig = customAttributeTypeBig
      ResolutionScopeBig = resolutionScopeBig }
