module internal FSharp.Compiler.CodeGen.DeltaIndexSizing

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

let private tableSize (tableRowCounts: int[]) (table: int) =
    tableRowCounts.[table]

let private totalRowCount
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (table: int)
    =
    let index = table
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
    (tables: int[])
    =
    tables
    |> Array.exists (fun table ->
        totalRowCount tableRowCounts externalRowCounts table >= maxValueExclusive)

let private codedBigness
    (tagBits: int)
    (tableRowCounts: int[])
    (externalRowCounts: int[])
    (isCompressed: bool)
    (tables: int[])
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
            [| DeltaTokens.tableTypeDef
               DeltaTokens.tableTypeRef
               DeltaTokens.tableTypeSpec |]

    let typeOrMethodDefBig =
        coded 1
            [| DeltaTokens.tableTypeDef
               DeltaTokens.tableMethodDef |]

    let hasConstantBig =
        coded 2
            [| DeltaTokens.tableField
               DeltaTokens.tableParam
               DeltaTokens.tableProperty |]

    let hasCustomAttributeBig =
        coded 5
            [| DeltaTokens.tableMethodDef
               DeltaTokens.tableField
               DeltaTokens.tableTypeRef
               DeltaTokens.tableTypeDef
               DeltaTokens.tableParam
               DeltaTokens.tableInterfaceImpl
               DeltaTokens.tableMemberRef
               DeltaTokens.tableModule
               DeltaTokens.tableDeclSecurity
               DeltaTokens.tableProperty
               DeltaTokens.tableEvent
               DeltaTokens.tableStandAloneSig
               DeltaTokens.tableModuleRef
               DeltaTokens.tableTypeSpec
               DeltaTokens.tableAssembly
               DeltaTokens.tableAssemblyRef
               DeltaTokens.tableFile
               DeltaTokens.tableExportedType
               DeltaTokens.tableManifestResource
               DeltaTokens.tableGenericParam
               DeltaTokens.tableGenericParamConstraint
               DeltaTokens.tableMethodSpec |]

    let hasFieldMarshalBig =
        coded 1
            [| DeltaTokens.tableField
               DeltaTokens.tableParam |]

    // ECMA-335 II.24.2.6: HasDeclSecurity - TypeDef(0), MethodDef(1), Assembly(2)
    let hasDeclSecurityBig =
        coded 2
            [| DeltaTokens.tableTypeDef
               DeltaTokens.tableMethodDef
               DeltaTokens.tableAssembly |]

    // ECMA-335 II.24.2.6: MemberRefParent - TypeDef(0), TypeRef(1), ModuleRef(2), MethodDef(3), TypeSpec(4)
    let memberRefParentBig =
        coded 3
            [| DeltaTokens.tableTypeDef
               DeltaTokens.tableTypeRef
               DeltaTokens.tableModuleRef
               DeltaTokens.tableMethodDef
               DeltaTokens.tableTypeSpec |]

    let hasSemanticsBig =
        coded 1
            [| DeltaTokens.tableEvent
               DeltaTokens.tableProperty |]

    let methodDefOrRefBig =
        coded 1
            [| DeltaTokens.tableMethodDef
               DeltaTokens.tableMemberRef |]

    let memberForwardedBig =
        coded 1
            [| DeltaTokens.tableField
               DeltaTokens.tableMethodDef |]

    let implementationBig =
        coded 2
            [| DeltaTokens.tableFile
               DeltaTokens.tableAssemblyRef
               DeltaTokens.tableExportedType |]

    let customAttributeTypeBig =
        coded 3
            [| DeltaTokens.tableMethodDef
               DeltaTokens.tableMemberRef |]

    let resolutionScopeBig =
        coded 2
            [| DeltaTokens.tableModule
               DeltaTokens.tableModuleRef
               DeltaTokens.tableAssemblyRef
               DeltaTokens.tableTypeRef |]

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
