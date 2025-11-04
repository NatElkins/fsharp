module internal FSharp.Compiler.HotReload.DeltaBuilder

open System
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.HotReload.DefinitionMap
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeDiff

let private checkedFiles (CheckedAssemblyAfterOptimization impls) =
    impls
    |> List.map (fun afterOpt -> afterOpt.ImplFile)

let private fileKey (CheckedImplFile(qualifiedNameOfFile = qual)) = qual.Text

let private buildLookup (files: CheckedImplFile list) =
    files |> List.map (fun file -> fileKey file, file) |> Map.ofList

let private emptyDefinitionMap: FSharpDefinitionMap =
    { Changes = []
      RudeEdits = [] }

let private mergeDefinitionMaps (left: FSharpDefinitionMap) (right: FSharpDefinitionMap) : FSharpDefinitionMap =
    { Changes = left.Changes @ right.Changes
      RudeEdits = left.RudeEdits @ right.RudeEdits }

let computeSymbolChanges
    (tcGlobals: TcGlobals)
    (baseline: CheckedAssemblyAfterOptimization)
    (updated: CheckedAssemblyAfterOptimization)
    : FSharpSymbolChanges =
    let baselineFiles = checkedFiles baseline
    let updatedFiles = checkedFiles updated

    let baselineLookup = buildLookup baselineFiles

    let definitionMap =
        (emptyDefinitionMap, updatedFiles)
        ||> List.fold (fun acc updatedFile ->
            match Map.tryFind (fileKey updatedFile) baselineLookup with
            | Some baselineFile ->
                let diff = diffImplementationFile tcGlobals baselineFile updatedFile
                let map = FSharpDefinitionMap.ofTypedTreeDiff diff
                mergeDefinitionMaps acc map
            | None ->
                // For now treat unmatched files as unsupported edits by generating a rude edit placeholder.
                let rudeEdit =
                    { Symbol = None
                      Kind = RudeEditKind.Unsupported
                      Message = $"File '{fileKey updatedFile}' is new or renamed; full rebuild required." }

                mergeDefinitionMaps acc { emptyDefinitionMap with RudeEdits = [ rudeEdit ] })

    FSharpSymbolChanges.ofDefinitionMap definitionMap

let private joinPath (segments: string list) = String.concat "." segments

let private deduplicate list = list |> List.fold (fun acc item -> if List.contains item acc then acc else item :: acc) [] |> List.rev

let mapSymbolChangesToDelta
    (baseline: FSharpEmitBaseline)
    (changes: FSharpSymbolChanges)
    : string list * MethodDefinitionKey list =

    let candidateEntityNames (symbol: SymbolId) =
        let segments = symbol.Path @ [ symbol.LogicalName ]

        let rec tails acc remaining =
            match remaining with
            | [] -> List.rev acc
            | _ :: tail as segs ->
                let name = joinPath segs
                tails (name :: acc) tail

        tails [] segments

    let tryResolveTypeName (names: string list) =
        names |> List.tryFind (fun name -> Map.containsKey name baseline.TypeTokens)

    let updatedTypes =
        changes
        |> FSharpSymbolChanges.entitySymbolsWithChanges
        |> List.choose (fun symbol ->
            symbol
            |> candidateEntityNames
            |> tryResolveTypeName)
        |> deduplicate

    let candidateContainingTypeNames (change: UpdatedSymbolChange) =
        let pathSuffixes =
            let rec tails acc segments =
                match segments with
                | [] -> List.rev acc
                | _ :: tail as segs -> tails (joinPath segs :: acc) tail

            tails [] change.Symbol.Path

        let explicitEntity =
            match change.ContainingEntity with
            | Some name -> [ name ]
            | None -> []

        deduplicate (explicitEntity @ pathSuffixes)

    let updatedMethods =
        changes.Updated
        |> List.choose (fun change ->
            match change.Kind with
            | SemanticEditKind.MethodBody when change.Symbol.Kind = SymbolKind.Value && not change.Symbol.IsSynthesized ->
                change
                |> candidateContainingTypeNames
                |> List.tryPick (fun typeName ->
                    baseline.MethodTokens
                    |> Map.toSeq
                    |> Seq.tryFind (fun (key, _) ->
                        key.DeclaringType = typeName
                        && String.Equals(key.Name, change.Symbol.LogicalName, StringComparison.Ordinal))
                    |> Option.map fst)
            | _ -> None)
        |> deduplicate

    updatedTypes, updatedMethods
