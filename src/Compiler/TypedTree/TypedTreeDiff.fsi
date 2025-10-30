// Copyright (c) Microsoft Corporation. All Rights Reserved.

module internal FSharp.Compiler.TypedTreeDiff

open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree

/// Describes the high-level category for a symbol participating in a hot reload edit.
[<RequireQualifiedAccess>]
type SymbolKind =
    | Value
    | Entity

/// Stable identity for values and entities tracked across baseline/hot reload sessions.
type SymbolId =
    { Path: string list
      LogicalName: string
      Stamp: Stamp
      Kind: SymbolKind
      IsSynthesized: bool }

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

type SemanticEdit =
    { Symbol: SymbolId
      Kind: SemanticEditKind
      BaselineHash: int option
      UpdatedHash: int option
      IsSynthesized: bool }

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
