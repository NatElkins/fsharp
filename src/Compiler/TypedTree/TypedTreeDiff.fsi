// Copyright (c) Microsoft Corporation. All Rights Reserved.

module internal FSharp.Compiler.TypedTreeDiff

open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree

/// Describes the high-level category for a symbol participating in a hot reload edit.
[<RequireQualifiedAccess>]
type SymbolKind =
    | Value
    | Entity

[<RequireQualifiedAccess>]
type SymbolMemberKind =
    | Method
    | PropertyGet of propertyName: string
    | PropertySet of propertyName: string
    | EventAdd of eventName: string
    | EventRemove of eventName: string
    | EventInvoke of eventName: string

/// Stable identity for values and entities tracked across baseline/hot reload sessions.
type SymbolId =
    { Path: string list
      LogicalName: string
      Stamp: Stamp
      Kind: SymbolKind
      MemberKind: SymbolMemberKind option
      IsSynthesized: bool
      CompiledName: string option
      TotalArgCount: int option
      GenericArity: int option }

    member QualifiedName: string

/// Classification of semantic edits that can be produced by the typed-tree diff.
[<RequireQualifiedAccess>]
type SemanticEditKind =
    | MethodBody
    | Insert
    | Delete
    | TypeDefinition

/// Reasons why an edit cannot be represented as an incremental delta.
[<RequireQualifiedAccess>]
type RudeEditKind =
    | SignatureChange
    | InlineChange
    | TypeLayoutChange
    | DeclarationAdded
    | DeclarationRemoved
    | Unsupported
    // Method addition restrictions (following Roslyn patterns)
    | InsertVirtual           // Virtual/abstract/override methods cannot be added
    | InsertConstructor       // Constructors cannot be added to existing types
    | InsertOperator          // User-defined operators cannot be added
    | InsertExplicitInterface // Explicit interface implementations cannot be added
    | InsertIntoInterface     // Members cannot be added to interfaces
    | FieldAdded              // Fields cannot be added (type layout change)

type SemanticEdit =
    { Symbol: SymbolId
      Kind: SemanticEditKind
      BaselineHash: int option
      UpdatedHash: int option
      IsSynthesized: bool
      ContainingEntity: string option }

type RudeEdit =
    { Symbol: SymbolId option
      Kind: RudeEditKind
      Message: string }

type TypedTreeDiffResult =
    { SemanticEdits: SemanticEdit list
      RudeEdits: RudeEdit list }

/// Computes semantic edits between two checked implementation files.
val diffImplementationFile:
    g: TcGlobals ->
    baseline: CheckedImplFile ->
    updated: CheckedImplFile ->
    TypedTreeDiffResult
