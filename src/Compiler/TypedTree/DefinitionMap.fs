module internal FSharp.Compiler.HotReload.DefinitionMap

open FSharp.Compiler.TypedTreeDiff

[<RequireQualifiedAccess>]
/// Classifies how a symbol changed between the baseline and the updated compilation.
type SymbolEditKind =
    | Added
    | Updated of SemanticEditKind
    | Deleted

[<RequireQualifiedAccess>]
/// Captures the change metadata for a single symbol, including hashes for change detection.
type SymbolChange =
    { Symbol: SymbolId
      EditKind: SymbolEditKind
      BaselineHash: int option
      UpdatedHash: int option }

[<RequireQualifiedAccess>]
/// Aggregates semantic edits and rude edits for the current compilation unit.
type FSharpDefinitionMap =
    { Changes: SymbolChange list
      RudeEdits: RudeEdit list }

module FSharpDefinitionMap =
    /// Convert a typed-tree diff result into a definition map suitable for downstream delta emission.
    let ofTypedTreeDiff (diff: TypedTreeDiffResult) : FSharpDefinitionMap =
        let changes: SymbolChange list =
            diff.SemanticEdits
            |> List.map (fun edit ->
                let editKind =
                    match edit.Kind with
                    | SemanticEditKind.Insert -> SymbolEditKind.Added
                    | SemanticEditKind.MethodBody
                    | SemanticEditKind.TypeDefinition -> SymbolEditKind.Updated edit.Kind
                    | SemanticEditKind.Delete -> SymbolEditKind.Deleted

                { Symbol = edit.Symbol
                  EditKind = editKind
                  BaselineHash = edit.BaselineHash
                  UpdatedHash = edit.UpdatedHash })

        { Changes = changes; RudeEdits = diff.RudeEdits }

    /// Retrieves all symbols newly added in the updated compilation.
    let added (map: FSharpDefinitionMap) : SymbolId list =
        map.Changes
        |> List.choose (fun (change: SymbolChange) ->
            match change.EditKind with
            | SymbolEditKind.Added -> Some change.Symbol
            | _ -> None)

    /// Retrieves all updated symbols along with the semantic edit classification.
    let updated (map: FSharpDefinitionMap) : (SymbolId * SemanticEditKind) list =
        map.Changes
        |> List.choose (fun (change: SymbolChange) ->
            match change.EditKind with
            | SymbolEditKind.Updated kind -> Some(change.Symbol, kind)
            | _ -> None)

    /// Retrieves all symbols deleted from the updated compilation.
    let deleted (map: FSharpDefinitionMap) : SymbolId list =
        map.Changes
        |> List.choose (fun (change: SymbolChange) ->
            match change.EditKind with
            | SymbolEditKind.Deleted -> Some change.Symbol
            | _ -> None)
