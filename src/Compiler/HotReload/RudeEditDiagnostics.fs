namespace FSharp.Compiler.HotReload

open FSharp.Compiler.TypedTreeDiff

/// Represents a user-facing diagnostic generated for a rude edit.
type internal RudeEditDiagnostic =
    { Id: string
      Message: string
      Kind: RudeEditKind
      SymbolName: string option }

module internal RudeEditDiagnostics =

    let private symbolDisplayName (symbol: SymbolId option) =
        symbol |> Option.map (fun s -> s.QualifiedName)

    let private formatMessage (kind: RudeEditKind) (symbolName: string option) fallback =
        let name = symbolName |> Option.defaultValue "the declaration"
        match kind with
        | RudeEditKind.SignatureChange ->
            sprintf "Changing the signature of '%s' is not supported during hot reload." name
        | RudeEditKind.InlineChange ->
            sprintf "Changing inline annotations for '%s' requires a rebuild." name
        | RudeEditKind.TypeLayoutChange ->
            sprintf "Changing the representation of '%s' requires a rebuild." name
        | RudeEditKind.DeclarationAdded ->
            sprintf "Adding a new declaration '%s' requires a rebuild." name
        | RudeEditKind.DeclarationRemoved ->
            sprintf "Removing the declaration '%s' requires a rebuild." name
        | RudeEditKind.InsertVirtual ->
            sprintf "Adding virtual, abstract, or override method '%s' is not supported." name
        | RudeEditKind.InsertConstructor ->
            sprintf "Adding constructor '%s' is not supported." name
        | RudeEditKind.InsertOperator ->
            sprintf "Adding user-defined operator '%s' is not supported." name
        | RudeEditKind.InsertExplicitInterface ->
            sprintf "Adding explicit interface implementation '%s' is not supported." name
        | RudeEditKind.InsertIntoInterface ->
            sprintf "Adding member '%s' to an interface is not supported." name
        | RudeEditKind.FieldAdded ->
            sprintf "Adding field '%s' is not supported (changes type layout)." name
        | RudeEditKind.Unsupported -> fallback

    let private diagnosticId kind =
        match kind with
        | RudeEditKind.SignatureChange -> "FSHRDL001"
        | RudeEditKind.InlineChange -> "FSHRDL002"
        | RudeEditKind.TypeLayoutChange -> "FSHRDL003"
        | RudeEditKind.DeclarationAdded -> "FSHRDL004"
        | RudeEditKind.DeclarationRemoved -> "FSHRDL005"
        | RudeEditKind.InsertVirtual -> "FSHRDL006"
        | RudeEditKind.InsertConstructor -> "FSHRDL007"
        | RudeEditKind.InsertOperator -> "FSHRDL008"
        | RudeEditKind.InsertExplicitInterface -> "FSHRDL009"
        | RudeEditKind.InsertIntoInterface -> "FSHRDL010"
        | RudeEditKind.FieldAdded -> "FSHRDL011"
        | RudeEditKind.Unsupported -> "FSHRDL099"

    let ofRudeEdit (edit: RudeEdit) : RudeEditDiagnostic =
        let symbolName = symbolDisplayName edit.Symbol
        { Id = diagnosticId edit.Kind
          Message = formatMessage edit.Kind symbolName edit.Message
          Kind = edit.Kind
          SymbolName = symbolName }

    let ofRudeEdits edits = edits |> List.map ofRudeEdit
