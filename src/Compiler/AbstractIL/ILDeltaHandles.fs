// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// F# types and utilities for hot reload delta metadata emission.
/// Common types (handles, heap offsets, coded index DUs) are defined in BinaryConstants (ilbinary.fs).
/// This module provides delta-specific utilities and re-exports.
module internal FSharp.Compiler.AbstractIL.ILDeltaHandles

open System
open FSharp.Compiler.AbstractIL.BinaryConstants

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
// Additional Coded Index Types (less frequently used)
// ============================================================================
// These are defined here rather than in BinaryConstants because they are
// primarily used by delta code and not needed for baseline IL writing.

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
// Utilities for metadata token manipulation, replacing MetadataTokens static methods.

/// Token arithmetic utilities (replaces System.Reflection.Metadata.Ecma335.MetadataTokens)
module DeltaTokens =

    /// Number of metadata tables defined in ECMA-335 (includes reserved slots)
    let TableCount = 64

    /// Extract the row number (lower 24 bits) from a metadata token
    let getRowNumber (token: int) = token &&& 0x00FFFFFF

    /// Extract the table index (upper 8 bits) from a metadata token
    let getTableIndex (token: int) = (token >>> 24) &&& 0xFF

    /// Create a metadata token from a TableName and row number.
    /// Token format: [table index : 8 bits][row number : 24 bits]
    /// Internal: TableName is from BinaryConstants which is internal.
    let internal makeToken (table: TableName) (rowNumber: int) =
        (table.Index <<< 24) ||| (rowNumber &&& 0x00FFFFFF)

    /// Create a metadata token from a raw table index (int) and row number.
    /// Use this for PDB tables which don't have TableName definitions,
    /// or when calling from outside the compiler assembly.
    let makeTokenFromIndex (tableIndex: int) (rowNumber: int) =
        (tableIndex <<< 24) ||| (rowNumber &&& 0x00FFFFFF)

    /// Create an EntityToken from a raw token value
    let toEntityToken (token: int) : EntityToken =
        { TableIndex = getTableIndex token
          RowId = getRowNumber token }

    /// Convert an EntityToken to a raw token value
    let fromEntityToken (entity: EntityToken) : int = entity.Token

    // -------------------------------------------------------------------------
    // Portable PDB Table Indices (not part of ECMA-335, defined in Portable PDB spec)
    // -------------------------------------------------------------------------
    // These tables are used for debug information in Portable PDB format.
    // They start at index 0x30 to avoid collision with ECMA-335 tables.
    // Reference: https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md

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

// ============================================================================
// Edit-and-Continue Operation Codes
// ============================================================================
// F# native enum for EncLog operation codes.
// Replaces System.Reflection.Metadata.Ecma335.EditAndContinueOperation.

/// Operation code for EncLog entries per ECMA-335.
/// Indicates whether a row is new (AddXxx) or an update (Default).
[<Struct; CustomEquality; NoComparison>]
type EditAndContinueOperation =
    | Default
    | AddMethod
    | AddField
    | AddParameter
    | AddProperty
    | AddEvent

    /// Get the numeric value for serialization
    member this.Value =
        match this with
        | Default -> 0
        | AddMethod -> 1
        | AddField -> 2
        | AddParameter -> 4
        | AddProperty -> 5
        | AddEvent -> 6

    override this.GetHashCode() = this.Value
    override this.Equals obj =
        match obj with
        | :? EditAndContinueOperation as other -> this.Value = other.Value
        | _ -> false

    interface IEquatable<EditAndContinueOperation> with
        member this.Equals other = this.Value = other.Value

// ============================================================================
// IL Exception Region Types
// ============================================================================
// These replace System.Reflection.Metadata.ExceptionRegion and ExceptionRegionKind

/// Kind of exception handling region in IL method body
type IlExceptionRegionKind =
    | Catch = 0
    | Filter = 1
    | Finally = 2
    | Fault = 4

/// Exception handling region in IL method body.
/// Replaces System.Reflection.Metadata.ExceptionRegion for delta emission.
[<Struct>]
type IlExceptionRegion =
    {
        Kind: IlExceptionRegionKind
        TryOffset: int
        TryLength: int
        HandlerOffset: int
        HandlerLength: int
        /// For Catch: the catch type token; for others: 0
        CatchTypeToken: int
        /// For Filter: the filter offset; for others: 0
        FilterOffset: int
    }
