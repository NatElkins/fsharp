module internal FSharp.Compiler.HotReload.SymbolChanges

open FSharp.Compiler.HotReload.DefinitionMap
open FSharp.Compiler.TypedTreeDiff

/// Categorises the kind of change applied to a synthesized member.
[<RequireQualifiedAccess>]
type SynthesizedMemberEditKind =
    | Added
    | Updated of SemanticEditKind
    | Deleted

/// Represents a single synthesized member edit along with hash metadata.
type SynthesizedMemberChange =
    { Symbol: SymbolId
      EditKind: SynthesizedMemberEditKind
      BaselineHash: int option
      UpdatedHash: int option }

/// Aggregated symbol changes derived from the typed-tree diff and definition map.
type FSharpSymbolChanges =
    { Added: SymbolId list
      Updated: (SymbolId * SemanticEditKind) list
      Deleted: SymbolId list
      Synthesized: SynthesizedMemberChange list
      RudeEdits: RudeEdit list }

module FSharpSymbolChanges =
    /// Builds `FSharpSymbolChanges` from a definition map, mirroring Roslyn's `SymbolChanges`.
    let ofDefinitionMap (definitionMap: FSharpDefinitionMap) : FSharpSymbolChanges =
        let synthesized =
            definitionMap
            |> FSharpDefinitionMap.synthesized
            |> List.map (fun change ->
                let editKind =
                    match change.EditKind with
                    | SymbolEditKind.Added -> SynthesizedMemberEditKind.Added
                    | SymbolEditKind.Updated kind -> SynthesizedMemberEditKind.Updated kind
                    | SymbolEditKind.Deleted -> SynthesizedMemberEditKind.Deleted

                { Symbol = change.Symbol
                  EditKind = editKind
                  BaselineHash = change.BaselineHash
                  UpdatedHash = change.UpdatedHash })

        { Added = FSharpDefinitionMap.added definitionMap
          Updated = FSharpDefinitionMap.updated definitionMap
          Deleted = FSharpDefinitionMap.deleted definitionMap
          Synthesized = synthesized
          RudeEdits = definitionMap.RudeEdits }

    /// Extracts synthesized members classified as added.
    let synthesizedAdded (changes: FSharpSymbolChanges) : SymbolId list =
        changes.Synthesized
        |> List.choose (fun change ->
            match change.EditKind with
            | SynthesizedMemberEditKind.Added -> Some change.Symbol
            | _ -> None)

    /// Extracts synthesized members classified as updated.
    let synthesizedUpdated (changes: FSharpSymbolChanges) : (SymbolId * SemanticEditKind) list =
        changes.Synthesized
        |> List.choose (fun change ->
            match change.EditKind with
            | SynthesizedMemberEditKind.Updated kind -> Some(change.Symbol, kind)
            | _ -> None)

    /// Extracts synthesized members classified as deleted.
    let synthesizedDeleted (changes: FSharpSymbolChanges) : SymbolId list =
        changes.Synthesized
        |> List.choose (fun change ->
            match change.EditKind with
            | SynthesizedMemberEditKind.Deleted -> Some change.Symbol
            | _ -> None)
