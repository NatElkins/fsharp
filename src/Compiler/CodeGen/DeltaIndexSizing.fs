module internal FSharp.Compiler.CodeGen.DeltaIndexSizing

open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.ILBinaryWriter

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

let private isSimpleIndexBig tableRowCounts table =
    tableSize tableRowCounts table >= 0x10000

let private codedBigness tagBits tableRowCounts tables =
    tables
    |> Array.exists (fun table -> tableSize tableRowCounts table >= (0x10000 >>> tagBits))

let compute (tableRowCounts: int[]) (heapSizes: MetadataHeapSizes) : CodedIndexSizes =
    let simpleIndexBig = Array.zeroCreate<bool> MetadataTokens.TableCount
    for index = 0 to tableRowCounts.Length - 1 do
        simpleIndexBig.[index] <- tableRowCounts.[index] >= 0x10000

    let typeDefOrRefBig =
        codedBigness 2 tableRowCounts
            [| TableIndex.TypeDef
               TableIndex.TypeRef
               TableIndex.TypeSpec |]

    let typeOrMethodDefBig =
        codedBigness 1 tableRowCounts
            [| TableIndex.TypeDef
               TableIndex.MethodDef |]

    let hasConstantBig =
        codedBigness 2 tableRowCounts
            [| TableIndex.Field
               TableIndex.Param
               TableIndex.Property |]

    let hasCustomAttributeBig =
        codedBigness 5 tableRowCounts
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
        codedBigness 1 tableRowCounts
            [| TableIndex.Field
               TableIndex.Param |]

    let hasDeclSecurityBig =
        codedBigness 2 tableRowCounts
            [| TableIndex.TypeDef
               TableIndex.MethodDef
               TableIndex.Assembly |]

    let memberRefParentBig =
        codedBigness 3 tableRowCounts
            [| TableIndex.TypeRef
               TableIndex.ModuleRef
               TableIndex.MethodDef
               TableIndex.TypeSpec |]

    let hasSemanticsBig =
        codedBigness 1 tableRowCounts
            [| TableIndex.Event
               TableIndex.Property |]

    let methodDefOrRefBig =
        codedBigness 1 tableRowCounts
            [| TableIndex.MethodDef
               TableIndex.MemberRef |]

    let memberForwardedBig =
        codedBigness 1 tableRowCounts
            [| TableIndex.Field
               TableIndex.MethodDef |]

    let implementationBig =
        codedBigness 2 tableRowCounts
            [| TableIndex.File
               TableIndex.AssemblyRef
               TableIndex.ExportedType |]

    let customAttributeTypeBig =
        codedBigness 3 tableRowCounts
            [| TableIndex.MethodDef
               TableIndex.MemberRef |]

    let resolutionScopeBig =
        codedBigness 2 tableRowCounts
            [| TableIndex.Module
               TableIndex.ModuleRef
               TableIndex.AssemblyRef
               TableIndex.TypeRef |]

    { StringsBig = heapSizes.StringHeapSize >= 0x10000
      GuidsBig = heapSizes.GuidHeapSize >= 0x10000
      BlobsBig = heapSizes.BlobHeapSize >= 0x10000
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
