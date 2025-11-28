// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// F# handle types for hot reload delta metadata emission.
/// These types replace System.Reflection.Metadata handle types (StringHandle, BlobHandle, etc.)
/// to enable fully F#-native delta serialization without SRM dependencies.
module FSharp.Compiler.AbstractIL.ILDeltaHandles

open System

// ============================================================================
// Heap Offset Wrapper Types
// ============================================================================
// These replace SRM's StringHandle, BlobHandle, etc.
// Using distinct struct types prevents mixing offsets from different heaps.

/// Offset into the #Strings heap
[<Struct>]
type StringOffset =
    | StringOffset of offset: int

    member this.Value = let (StringOffset v) = this in v

    static member Zero = StringOffset 0

/// Offset into the #Blob heap
[<Struct>]
type BlobOffset =
    | BlobOffset of offset: int

    member this.Value = let (BlobOffset v) = this in v

    static member Zero = BlobOffset 0

/// Index into the #GUID heap (1-based, 0 = nil)
[<Struct>]
type GuidIndex =
    | GuidIndex of index: int

    member this.Value = let (GuidIndex v) = this in v

    static member Zero = GuidIndex 0

/// Offset into the #US (user string) heap
[<Struct>]
type UserStringOffset =
    | UserStringOffset of offset: int

    member this.Value = let (UserStringOffset v) = this in v

    static member Zero = UserStringOffset 0

// ============================================================================
// Table Row Handle Types
// ============================================================================
// These replace SRM's EntityHandle and specific handle types.
// Each handle wraps a 1-based row ID for its respective table.

/// Handle to a row in the Module table (table 0x00)
[<Struct>]
type ModuleHandle =
    | ModuleHandle of rowId: int

    member this.RowId = let (ModuleHandle v) = this in v

/// Handle to a row in the TypeRef table (table 0x01)
[<Struct>]
type TypeRefHandle =
    | TypeRefHandle of rowId: int

    member this.RowId = let (TypeRefHandle v) = this in v

/// Handle to a row in the TypeDef table (table 0x02)
[<Struct>]
type TypeDefHandle =
    | TypeDefHandle of rowId: int

    member this.RowId = let (TypeDefHandle v) = this in v

/// Handle to a row in the Field table (table 0x04)
[<Struct>]
type FieldHandle =
    | FieldHandle of rowId: int

    member this.RowId = let (FieldHandle v) = this in v

/// Handle to a row in the MethodDef table (table 0x06)
[<Struct>]
type MethodDefHandle =
    | MethodDefHandle of rowId: int

    member this.RowId = let (MethodDefHandle v) = this in v

/// Handle to a row in the Param table (table 0x08)
[<Struct>]
type ParamHandle =
    | ParamHandle of rowId: int

    member this.RowId = let (ParamHandle v) = this in v

/// Handle to a row in the InterfaceImpl table (table 0x09)
[<Struct>]
type InterfaceImplHandle =
    | InterfaceImplHandle of rowId: int

    member this.RowId = let (InterfaceImplHandle v) = this in v

/// Handle to a row in the MemberRef table (table 0x0A)
[<Struct>]
type MemberRefHandle =
    | MemberRefHandle of rowId: int

    member this.RowId = let (MemberRefHandle v) = this in v

/// Handle to a row in the Constant table (table 0x0B)
[<Struct>]
type ConstantHandle =
    | ConstantHandle of rowId: int

    member this.RowId = let (ConstantHandle v) = this in v

/// Handle to a row in the CustomAttribute table (table 0x0C)
[<Struct>]
type CustomAttributeHandle =
    | CustomAttributeHandle of rowId: int

    member this.RowId = let (CustomAttributeHandle v) = this in v

/// Handle to a row in the FieldMarshal table (table 0x0D)
[<Struct>]
type FieldMarshalHandle =
    | FieldMarshalHandle of rowId: int

    member this.RowId = let (FieldMarshalHandle v) = this in v

/// Handle to a row in the DeclSecurity table (table 0x0E)
[<Struct>]
type DeclSecurityHandle =
    | DeclSecurityHandle of rowId: int

    member this.RowId = let (DeclSecurityHandle v) = this in v

/// Handle to a row in the ClassLayout table (table 0x0F)
[<Struct>]
type ClassLayoutHandle =
    | ClassLayoutHandle of rowId: int

    member this.RowId = let (ClassLayoutHandle v) = this in v

/// Handle to a row in the FieldLayout table (table 0x10)
[<Struct>]
type FieldLayoutHandle =
    | FieldLayoutHandle of rowId: int

    member this.RowId = let (FieldLayoutHandle v) = this in v

/// Handle to a row in the StandAloneSig table (table 0x11)
[<Struct>]
type StandAloneSigHandle =
    | StandAloneSigHandle of rowId: int

    member this.RowId = let (StandAloneSigHandle v) = this in v

/// Handle to a row in the EventMap table (table 0x12)
[<Struct>]
type EventMapHandle =
    | EventMapHandle of rowId: int

    member this.RowId = let (EventMapHandle v) = this in v

/// Handle to a row in the Event table (table 0x14)
[<Struct>]
type EventHandle =
    | EventHandle of rowId: int

    member this.RowId = let (EventHandle v) = this in v

/// Handle to a row in the PropertyMap table (table 0x15)
[<Struct>]
type PropertyMapHandle =
    | PropertyMapHandle of rowId: int

    member this.RowId = let (PropertyMapHandle v) = this in v

/// Handle to a row in the Property table (table 0x17)
[<Struct>]
type PropertyHandle =
    | PropertyHandle of rowId: int

    member this.RowId = let (PropertyHandle v) = this in v

/// Handle to a row in the MethodSemantics table (table 0x18)
[<Struct>]
type MethodSemanticsHandle =
    | MethodSemanticsHandle of rowId: int

    member this.RowId = let (MethodSemanticsHandle v) = this in v

/// Handle to a row in the MethodImpl table (table 0x19)
[<Struct>]
type MethodImplHandle =
    | MethodImplHandle of rowId: int

    member this.RowId = let (MethodImplHandle v) = this in v

/// Handle to a row in the ModuleRef table (table 0x1A)
[<Struct>]
type ModuleRefHandle =
    | ModuleRefHandle of rowId: int

    member this.RowId = let (ModuleRefHandle v) = this in v

/// Handle to a row in the TypeSpec table (table 0x1B)
[<Struct>]
type TypeSpecHandle =
    | TypeSpecHandle of rowId: int

    member this.RowId = let (TypeSpecHandle v) = this in v

/// Handle to a row in the ImplMap table (table 0x1C)
[<Struct>]
type ImplMapHandle =
    | ImplMapHandle of rowId: int

    member this.RowId = let (ImplMapHandle v) = this in v

/// Handle to a row in the FieldRVA table (table 0x1D)
[<Struct>]
type FieldRVAHandle =
    | FieldRVAHandle of rowId: int

    member this.RowId = let (FieldRVAHandle v) = this in v

/// Handle to a row in the Assembly table (table 0x20)
[<Struct>]
type AssemblyHandle =
    | AssemblyHandle of rowId: int

    member this.RowId = let (AssemblyHandle v) = this in v

/// Handle to a row in the AssemblyRef table (table 0x23)
[<Struct>]
type AssemblyRefHandle =
    | AssemblyRefHandle of rowId: int

    member this.RowId = let (AssemblyRefHandle v) = this in v

/// Handle to a row in the File table (table 0x26)
[<Struct>]
type FileHandle =
    | FileHandle of rowId: int

    member this.RowId = let (FileHandle v) = this in v

/// Handle to a row in the ExportedType table (table 0x27)
[<Struct>]
type ExportedTypeHandle =
    | ExportedTypeHandle of rowId: int

    member this.RowId = let (ExportedTypeHandle v) = this in v

/// Handle to a row in the ManifestResource table (table 0x28)
[<Struct>]
type ManifestResourceHandle =
    | ManifestResourceHandle of rowId: int

    member this.RowId = let (ManifestResourceHandle v) = this in v

/// Handle to a row in the NestedClass table (table 0x29)
[<Struct>]
type NestedClassHandle =
    | NestedClassHandle of rowId: int

    member this.RowId = let (NestedClassHandle v) = this in v

/// Handle to a row in the GenericParam table (table 0x2A)
[<Struct>]
type GenericParamHandle =
    | GenericParamHandle of rowId: int

    member this.RowId = let (GenericParamHandle v) = this in v

/// Handle to a row in the MethodSpec table (table 0x2B)
[<Struct>]
type MethodSpecHandle =
    | MethodSpecHandle of rowId: int

    member this.RowId = let (MethodSpecHandle v) = this in v

/// Handle to a row in the GenericParamConstraint table (table 0x2C)
[<Struct>]
type GenericParamConstraintHandle =
    | GenericParamConstraintHandle of rowId: int

    member this.RowId = let (GenericParamConstraintHandle v) = this in v

// ============================================================================
// Entity Token
// ============================================================================
// Generic token representation for EncLog/EncMap entries

/// Represents a metadata token as table index and row ID
/// Used for EncLog and EncMap entries
[<Struct>]
type EntityToken =
    { TableIndex: int
      RowId: int }

    /// Creates a token from table index and row ID
    static member Create(tableIndex: int, rowId: int) = { TableIndex = tableIndex; RowId = rowId }

    /// Gets the full 32-bit token value (table << 24 | rowId)
    member this.Token = (this.TableIndex <<< 24) ||| (this.RowId &&& 0x00FFFFFF)

// ============================================================================
// Coded Index Types (Discriminated Unions)
// ============================================================================
// These replace pattern matching on HandleKind in DeltaMetadataTables.fs
// See ECMA-335 II.24.2.6 for coded index specifications

/// TypeDefOrRef coded index (2-bit tag)
/// Tag: TypeDef=0, TypeRef=1, TypeSpec=2
type TypeDefOrRef =
    | TDR_TypeDef of TypeDefHandle
    | TDR_TypeRef of TypeRefHandle
    | TDR_TypeSpec of TypeSpecHandle

    /// Gets the table index for this coded index
    member this.TableIndex =
        match this with
        | TDR_TypeDef _ -> 0x02
        | TDR_TypeRef _ -> 0x01
        | TDR_TypeSpec _ -> 0x1B

    /// Gets the row ID
    member this.RowId =
        match this with
        | TDR_TypeDef(TypeDefHandle rid) -> rid
        | TDR_TypeRef(TypeRefHandle rid) -> rid
        | TDR_TypeSpec(TypeSpecHandle rid) -> rid

/// HasConstant coded index (2-bit tag)
/// Tag: Field=0, Param=1, Property=2
type HasConstant =
    | HC_Field of FieldHandle
    | HC_Param of ParamHandle
    | HC_Property of PropertyHandle

    member this.TableIndex =
        match this with
        | HC_Field _ -> 0x04
        | HC_Param _ -> 0x08
        | HC_Property _ -> 0x17

    member this.RowId =
        match this with
        | HC_Field(FieldHandle rid) -> rid
        | HC_Param(ParamHandle rid) -> rid
        | HC_Property(PropertyHandle rid) -> rid

/// HasCustomAttribute coded index (5-bit tag) - 22 possible parent types
/// See ECMA-335 II.24.2.6 Table II.12
type HasCustomAttribute =
    | HCA_MethodDef of MethodDefHandle
    | HCA_Field of FieldHandle
    | HCA_TypeRef of TypeRefHandle
    | HCA_TypeDef of TypeDefHandle
    | HCA_Param of ParamHandle
    | HCA_InterfaceImpl of InterfaceImplHandle
    | HCA_MemberRef of MemberRefHandle
    | HCA_Module of ModuleHandle
    | HCA_DeclSecurity of DeclSecurityHandle
    | HCA_Property of PropertyHandle
    | HCA_Event of EventHandle
    | HCA_StandAloneSig of StandAloneSigHandle
    | HCA_ModuleRef of ModuleRefHandle
    | HCA_TypeSpec of TypeSpecHandle
    | HCA_Assembly of AssemblyHandle
    | HCA_AssemblyRef of AssemblyRefHandle
    | HCA_File of FileHandle
    | HCA_ExportedType of ExportedTypeHandle
    | HCA_ManifestResource of ManifestResourceHandle
    | HCA_GenericParam of GenericParamHandle
    | HCA_GenericParamConstraint of GenericParamConstraintHandle
    | HCA_MethodSpec of MethodSpecHandle

    /// Gets the coded index tag value per ECMA-335 II.24.2.6
    member this.CodedTag =
        match this with
        | HCA_MethodDef _ -> 0
        | HCA_Field _ -> 1
        | HCA_TypeRef _ -> 2
        | HCA_TypeDef _ -> 3
        | HCA_Param _ -> 4
        | HCA_InterfaceImpl _ -> 5
        | HCA_MemberRef _ -> 6
        | HCA_Module _ -> 7
        | HCA_DeclSecurity _ -> 8
        | HCA_Property _ -> 9
        | HCA_Event _ -> 10
        | HCA_StandAloneSig _ -> 11
        | HCA_ModuleRef _ -> 12
        | HCA_TypeSpec _ -> 13
        | HCA_Assembly _ -> 14
        | HCA_AssemblyRef _ -> 15
        | HCA_File _ -> 16
        | HCA_ExportedType _ -> 17
        | HCA_ManifestResource _ -> 18
        | HCA_GenericParam _ -> 19
        | HCA_GenericParamConstraint _ -> 20
        | HCA_MethodSpec _ -> 21

    member this.TableIndex =
        match this with
        | HCA_MethodDef _ -> 0x06
        | HCA_Field _ -> 0x04
        | HCA_TypeRef _ -> 0x01
        | HCA_TypeDef _ -> 0x02
        | HCA_Param _ -> 0x08
        | HCA_InterfaceImpl _ -> 0x09
        | HCA_MemberRef _ -> 0x0A
        | HCA_Module _ -> 0x00
        | HCA_DeclSecurity _ -> 0x0E
        | HCA_Property _ -> 0x17
        | HCA_Event _ -> 0x14
        | HCA_StandAloneSig _ -> 0x11
        | HCA_ModuleRef _ -> 0x1A
        | HCA_TypeSpec _ -> 0x1B
        | HCA_Assembly _ -> 0x20
        | HCA_AssemblyRef _ -> 0x23
        | HCA_File _ -> 0x26
        | HCA_ExportedType _ -> 0x27
        | HCA_ManifestResource _ -> 0x28
        | HCA_GenericParam _ -> 0x2A
        | HCA_GenericParamConstraint _ -> 0x2C
        | HCA_MethodSpec _ -> 0x2B

    member this.RowId =
        match this with
        | HCA_MethodDef(MethodDefHandle rid) -> rid
        | HCA_Field(FieldHandle rid) -> rid
        | HCA_TypeRef(TypeRefHandle rid) -> rid
        | HCA_TypeDef(TypeDefHandle rid) -> rid
        | HCA_Param(ParamHandle rid) -> rid
        | HCA_InterfaceImpl(InterfaceImplHandle rid) -> rid
        | HCA_MemberRef(MemberRefHandle rid) -> rid
        | HCA_Module(ModuleHandle rid) -> rid
        | HCA_DeclSecurity(DeclSecurityHandle rid) -> rid
        | HCA_Property(PropertyHandle rid) -> rid
        | HCA_Event(EventHandle rid) -> rid
        | HCA_StandAloneSig(StandAloneSigHandle rid) -> rid
        | HCA_ModuleRef(ModuleRefHandle rid) -> rid
        | HCA_TypeSpec(TypeSpecHandle rid) -> rid
        | HCA_Assembly(AssemblyHandle rid) -> rid
        | HCA_AssemblyRef(AssemblyRefHandle rid) -> rid
        | HCA_File(FileHandle rid) -> rid
        | HCA_ExportedType(ExportedTypeHandle rid) -> rid
        | HCA_ManifestResource(ManifestResourceHandle rid) -> rid
        | HCA_GenericParam(GenericParamHandle rid) -> rid
        | HCA_GenericParamConstraint(GenericParamConstraintHandle rid) -> rid
        | HCA_MethodSpec(MethodSpecHandle rid) -> rid

/// HasFieldMarshal coded index (1-bit tag)
/// Tag: Field=0, Param=1
type HasFieldMarshal =
    | HFM_Field of FieldHandle
    | HFM_Param of ParamHandle

    member this.TableIndex =
        match this with
        | HFM_Field _ -> 0x04
        | HFM_Param _ -> 0x08

    member this.RowId =
        match this with
        | HFM_Field(FieldHandle rid) -> rid
        | HFM_Param(ParamHandle rid) -> rid

/// HasDeclSecurity coded index (2-bit tag)
/// Tag: TypeDef=0, MethodDef=1, Assembly=2
type HasDeclSecurity =
    | HDS_TypeDef of TypeDefHandle
    | HDS_MethodDef of MethodDefHandle
    | HDS_Assembly of AssemblyHandle

    member this.TableIndex =
        match this with
        | HDS_TypeDef _ -> 0x02
        | HDS_MethodDef _ -> 0x06
        | HDS_Assembly _ -> 0x20

    member this.RowId =
        match this with
        | HDS_TypeDef(TypeDefHandle rid) -> rid
        | HDS_MethodDef(MethodDefHandle rid) -> rid
        | HDS_Assembly(AssemblyHandle rid) -> rid

/// MemberRefParent coded index (3-bit tag)
/// Tag: TypeDef=0, TypeRef=1, ModuleRef=2, MethodDef=3, TypeSpec=4
type MemberRefParent =
    | MRP_TypeDef of TypeDefHandle
    | MRP_TypeRef of TypeRefHandle
    | MRP_ModuleRef of ModuleRefHandle
    | MRP_MethodDef of MethodDefHandle
    | MRP_TypeSpec of TypeSpecHandle

    /// Gets the coded index tag value per ECMA-335 II.24.2.6
    member this.CodedTag =
        match this with
        | MRP_TypeDef _ -> 0
        | MRP_TypeRef _ -> 1
        | MRP_ModuleRef _ -> 2
        | MRP_MethodDef _ -> 3
        | MRP_TypeSpec _ -> 4

    member this.TableIndex =
        match this with
        | MRP_TypeDef _ -> 0x02
        | MRP_TypeRef _ -> 0x01
        | MRP_ModuleRef _ -> 0x1A
        | MRP_MethodDef _ -> 0x06
        | MRP_TypeSpec _ -> 0x1B

    member this.RowId =
        match this with
        | MRP_TypeDef(TypeDefHandle rid) -> rid
        | MRP_TypeRef(TypeRefHandle rid) -> rid
        | MRP_ModuleRef(ModuleRefHandle rid) -> rid
        | MRP_MethodDef(MethodDefHandle rid) -> rid
        | MRP_TypeSpec(TypeSpecHandle rid) -> rid

/// HasSemantics coded index (1-bit tag)
/// Tag: Event=0, Property=1
type HasSemantics =
    | HS_Event of EventHandle
    | HS_Property of PropertyHandle

    member this.TableIndex =
        match this with
        | HS_Event _ -> 0x14
        | HS_Property _ -> 0x17

    member this.RowId =
        match this with
        | HS_Event(EventHandle rid) -> rid
        | HS_Property(PropertyHandle rid) -> rid

/// MethodDefOrRef coded index (1-bit tag)
/// Tag: MethodDef=0, MemberRef=1
type MethodDefOrRef =
    | MDOR_MethodDef of MethodDefHandle
    | MDOR_MemberRef of MemberRefHandle

    member this.TableIndex =
        match this with
        | MDOR_MethodDef _ -> 0x06
        | MDOR_MemberRef _ -> 0x0A

    member this.RowId =
        match this with
        | MDOR_MethodDef(MethodDefHandle rid) -> rid
        | MDOR_MemberRef(MemberRefHandle rid) -> rid

/// MemberForwarded coded index (1-bit tag)
/// Tag: Field=0, MethodDef=1
type MemberForwarded =
    | MF_Field of FieldHandle
    | MF_MethodDef of MethodDefHandle

    member this.TableIndex =
        match this with
        | MF_Field _ -> 0x04
        | MF_MethodDef _ -> 0x06

    member this.RowId =
        match this with
        | MF_Field(FieldHandle rid) -> rid
        | MF_MethodDef(MethodDefHandle rid) -> rid

/// Implementation coded index (2-bit tag)
/// Tag: File=0, AssemblyRef=1, ExportedType=2
type Implementation =
    | IMP_File of FileHandle
    | IMP_AssemblyRef of AssemblyRefHandle
    | IMP_ExportedType of ExportedTypeHandle

    member this.TableIndex =
        match this with
        | IMP_File _ -> 0x26
        | IMP_AssemblyRef _ -> 0x23
        | IMP_ExportedType _ -> 0x27

    member this.RowId =
        match this with
        | IMP_File(FileHandle rid) -> rid
        | IMP_AssemblyRef(AssemblyRefHandle rid) -> rid
        | IMP_ExportedType(ExportedTypeHandle rid) -> rid

/// CustomAttributeType coded index (3-bit tag, only 2 valid values)
/// Tag: MethodDef=2, MemberRef=3 (0,1,4 are unused)
type CustomAttributeType =
    | CAT_MethodDef of MethodDefHandle
    | CAT_MemberRef of MemberRefHandle

    /// Gets the coded index tag value per ECMA-335 II.24.2.6
    member this.CodedTag =
        match this with
        | CAT_MethodDef _ -> 2
        | CAT_MemberRef _ -> 3

    member this.TableIndex =
        match this with
        | CAT_MethodDef _ -> 0x06
        | CAT_MemberRef _ -> 0x0A

    member this.RowId =
        match this with
        | CAT_MethodDef(MethodDefHandle rid) -> rid
        | CAT_MemberRef(MemberRefHandle rid) -> rid

/// ResolutionScope coded index (2-bit tag)
/// Tag: Module=0, ModuleRef=1, AssemblyRef=2, TypeRef=3
type ResolutionScope =
    | RS_Module of ModuleHandle
    | RS_ModuleRef of ModuleRefHandle
    | RS_AssemblyRef of AssemblyRefHandle
    | RS_TypeRef of TypeRefHandle

    /// Gets the coded index tag value per ECMA-335 II.24.2.6
    member this.CodedTag =
        match this with
        | RS_Module _ -> 0
        | RS_ModuleRef _ -> 1
        | RS_AssemblyRef _ -> 2
        | RS_TypeRef _ -> 3

    member this.TableIndex =
        match this with
        | RS_Module _ -> 0x00
        | RS_ModuleRef _ -> 0x1A
        | RS_AssemblyRef _ -> 0x23
        | RS_TypeRef _ -> 0x01

    member this.RowId =
        match this with
        | RS_Module(ModuleHandle rid) -> rid
        | RS_ModuleRef(ModuleRefHandle rid) -> rid
        | RS_AssemblyRef(AssemblyRefHandle rid) -> rid
        | RS_TypeRef(TypeRefHandle rid) -> rid

/// TypeOrMethodDef coded index (1-bit tag)
/// Tag: TypeDef=0, MethodDef=1
type TypeOrMethodDef =
    | TOMD_TypeDef of TypeDefHandle
    | TOMD_MethodDef of MethodDefHandle

    member this.TableIndex =
        match this with
        | TOMD_TypeDef _ -> 0x02
        | TOMD_MethodDef _ -> 0x06

    member this.RowId =
        match this with
        | TOMD_TypeDef(TypeDefHandle rid) -> rid
        | TOMD_MethodDef(MethodDefHandle rid) -> rid

// ============================================================================
// DeltaTokens Module
// ============================================================================
// Utilities for metadata token manipulation, replacing MetadataTokens static methods

/// Token arithmetic utilities (replaces System.Reflection.Metadata.Ecma335.MetadataTokens)
module DeltaTokens =
    /// Number of metadata tables defined in ECMA-335
    let TableCount = 64

    /// Extract the row number from a metadata token
    let getRowNumber (token: int) = token &&& 0x00FFFFFF

    /// Extract the table index from a metadata token
    let getTableIndex (token: int) = (token >>> 24) &&& 0xFF

    /// Create a metadata token from table index and row number
    let makeToken (tableIndex: int) (rowNumber: int) =
        (tableIndex <<< 24) ||| (rowNumber &&& 0x00FFFFFF)

    /// Create an EntityToken from a raw token value
    let toEntityToken (token: int) : EntityToken =
        { TableIndex = getTableIndex token
          RowId = getRowNumber token }

    /// Convert an EntityToken to a raw token value
    let fromEntityToken (entity: EntityToken) : int = entity.Token

    // Table indices (matching TableIndex enum values)
    let tableModule = 0x00
    let tableTypeRef = 0x01
    let tableTypeDef = 0x02
    let tableField = 0x04
    let tableMethodDef = 0x06
    let tableParam = 0x08
    let tableInterfaceImpl = 0x09
    let tableMemberRef = 0x0A
    let tableConstant = 0x0B
    let tableCustomAttribute = 0x0C
    let tableFieldMarshal = 0x0D
    let tableDeclSecurity = 0x0E
    let tableClassLayout = 0x0F
    let tableFieldLayout = 0x10
    let tableStandAloneSig = 0x11
    let tableEventMap = 0x12
    let tableEvent = 0x14
    let tablePropertyMap = 0x15
    let tableProperty = 0x17
    let tableMethodSemantics = 0x18
    let tableMethodImpl = 0x19
    let tableModuleRef = 0x1A
    let tableTypeSpec = 0x1B
    let tableImplMap = 0x1C
    let tableFieldRVA = 0x1D
    let tableAssembly = 0x20
    let tableAssemblyRef = 0x23
    let tableFile = 0x26
    let tableExportedType = 0x27
    let tableManifestResource = 0x28
    let tableNestedClass = 0x29
    let tableGenericParam = 0x2A
    let tableMethodSpec = 0x2B
    let tableGenericParamConstraint = 0x2C
    let tableEncLog = 0x1E
    let tableEncMap = 0x1F

    // PDB table indices (Portable PDB spec)
    let tableDocument = 0x30
    let tableMethodDebugInformation = 0x31
    let tableLocalScope = 0x32
    let tableLocalVariable = 0x33
    let tableLocalConstant = 0x34
    let tableImportScope = 0x35
    let tableStateMachineMethod = 0x36
    let tableCustomDebugInformation = 0x37

// ============================================================================
// Conversion Helpers
// ============================================================================
// Functions to convert between F# handles and raw values

module HandleConversions =
    /// Create a HasCustomAttribute from table index and row ID
    /// Returns None for invalid table indices
    let tryMakeHasCustomAttribute (tableIndex: int) (rowId: int) : HasCustomAttribute option =
        match tableIndex with
        | 0x06 -> Some(HCA_MethodDef(MethodDefHandle rowId))
        | 0x04 -> Some(HCA_Field(FieldHandle rowId))
        | 0x01 -> Some(HCA_TypeRef(TypeRefHandle rowId))
        | 0x02 -> Some(HCA_TypeDef(TypeDefHandle rowId))
        | 0x08 -> Some(HCA_Param(ParamHandle rowId))
        | 0x09 -> Some(HCA_InterfaceImpl(InterfaceImplHandle rowId))
        | 0x0A -> Some(HCA_MemberRef(MemberRefHandle rowId))
        | 0x00 -> Some(HCA_Module(ModuleHandle rowId))
        | 0x0E -> Some(HCA_DeclSecurity(DeclSecurityHandle rowId))
        | 0x17 -> Some(HCA_Property(PropertyHandle rowId))
        | 0x14 -> Some(HCA_Event(EventHandle rowId))
        | 0x11 -> Some(HCA_StandAloneSig(StandAloneSigHandle rowId))
        | 0x1A -> Some(HCA_ModuleRef(ModuleRefHandle rowId))
        | 0x1B -> Some(HCA_TypeSpec(TypeSpecHandle rowId))
        | 0x20 -> Some(HCA_Assembly(AssemblyHandle rowId))
        | 0x23 -> Some(HCA_AssemblyRef(AssemblyRefHandle rowId))
        | 0x26 -> Some(HCA_File(FileHandle rowId))
        | 0x27 -> Some(HCA_ExportedType(ExportedTypeHandle rowId))
        | 0x28 -> Some(HCA_ManifestResource(ManifestResourceHandle rowId))
        | 0x2A -> Some(HCA_GenericParam(GenericParamHandle rowId))
        | 0x2C -> Some(HCA_GenericParamConstraint(GenericParamConstraintHandle rowId))
        | 0x2B -> Some(HCA_MethodSpec(MethodSpecHandle rowId))
        | _ -> None

    /// Create a ResolutionScope from table index and row ID
    let tryMakeResolutionScope (tableIndex: int) (rowId: int) : ResolutionScope option =
        match tableIndex with
        | 0x00 -> Some(RS_Module(ModuleHandle rowId))
        | 0x1A -> Some(RS_ModuleRef(ModuleRefHandle rowId))
        | 0x23 -> Some(RS_AssemblyRef(AssemblyRefHandle rowId))
        | 0x01 -> Some(RS_TypeRef(TypeRefHandle rowId))
        | _ -> None

    /// Create a MemberRefParent from table index and row ID
    let tryMakeMemberRefParent (tableIndex: int) (rowId: int) : MemberRefParent option =
        match tableIndex with
        | 0x02 -> Some(MRP_TypeDef(TypeDefHandle rowId))
        | 0x01 -> Some(MRP_TypeRef(TypeRefHandle rowId))
        | 0x1A -> Some(MRP_ModuleRef(ModuleRefHandle rowId))
        | 0x06 -> Some(MRP_MethodDef(MethodDefHandle rowId))
        | 0x1B -> Some(MRP_TypeSpec(TypeSpecHandle rowId))
        | _ -> None

    /// Create a CustomAttributeType from table index and row ID
    let tryMakeCustomAttributeType (tableIndex: int) (rowId: int) : CustomAttributeType option =
        match tableIndex with
        | 0x06 -> Some(CAT_MethodDef(MethodDefHandle rowId))
        | 0x0A -> Some(CAT_MemberRef(MemberRefHandle rowId))
        | _ -> None

    /// Create a TypeDefOrRef from table index and row ID
    let tryMakeTypeDefOrRef (tableIndex: int) (rowId: int) : TypeDefOrRef option =
        match tableIndex with
        | 0x02 -> Some(TDR_TypeDef(TypeDefHandle rowId))
        | 0x01 -> Some(TDR_TypeRef(TypeRefHandle rowId))
        | 0x1B -> Some(TDR_TypeSpec(TypeSpecHandle rowId))
        | _ -> None
