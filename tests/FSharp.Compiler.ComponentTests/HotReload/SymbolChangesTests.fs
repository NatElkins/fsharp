namespace FSharp.Compiler.ComponentTests.HotReload

open Xunit
open FSharp.Compiler.TypedTreeDiff
open FSharp.Compiler.HotReload.DefinitionMap
open FSharp.Compiler.HotReload.SymbolChanges

module SymbolChangesTests =

    let private symbol path name stamp kind isSynthesized : SymbolId =
        { Path = path
          LogicalName = name
          Stamp = stamp
          Kind = kind
          IsSynthesized = isSynthesized }

    let private diff edits rude =
        { TypedTreeDiffResult.SemanticEdits = edits
          RudeEdits = rude }

    [<Fact>]
    let ``synthesized updates are partitioned separately`` () =
        let synthesizedEdit : SemanticEdit =
            { Symbol = symbol [ "Module" ] "closure@4" 7L SymbolKind.Value true
              Kind = SemanticEditKind.MethodBody
              BaselineHash = Some 10
              UpdatedHash = Some 20
              IsSynthesized = true }

        let regularEdit : SemanticEdit =
            { Symbol = symbol [ "Module" ] "Value" 8L SymbolKind.Value false
              Kind = SemanticEditKind.MethodBody
              BaselineHash = Some 3
              UpdatedHash = Some 4
              IsSynthesized = false }

        let definitionMap = diff [ synthesizedEdit; regularEdit ] [] |> FSharpDefinitionMap.ofTypedTreeDiff
        let symbolChanges = FSharpSymbolChanges.ofDefinitionMap definitionMap

        Assert.Equal<(SymbolId * SemanticEditKind) list>([ synthesizedEdit.Symbol, SemanticEditKind.MethodBody ], FSharpDefinitionMap.synthesizedUpdated definitionMap)

        let synthesizedUpdated = FSharpSymbolChanges.synthesizedUpdated symbolChanges
        Assert.Single synthesizedUpdated |> ignore
        let (symbol, editKind) = List.head synthesizedUpdated
        Assert.True(symbol.IsSynthesized)
        Assert.Equal(SemanticEditKind.MethodBody, editKind)

        // Regular edits should still appear in the aggregated updated list.
        Assert.Contains(symbolChanges.Updated, fun (symbol, _) -> symbol.QualifiedName = "Module.Value")
