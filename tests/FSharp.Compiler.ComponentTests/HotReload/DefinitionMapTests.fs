namespace FSharp.Compiler.ComponentTests.HotReload

open Xunit
open FSharp.Compiler.TypedTreeDiff
open FSharp.Compiler.HotReload.DefinitionMap

module DefinitionMapTests =

    let private symbol path name stamp kind : SymbolId =
        { Path = path
          LogicalName = name
          Stamp = stamp
          Kind = kind }

    let private diffResult edits rude =
        { TypedTreeDiffResult.SemanticEdits = edits
          RudeEdits = rude }

    [<Fact>]
    let ``added edit surfaces in definition map`` () =
        let edit =
            { Symbol = symbol [ "Module" ] "AddedValue" 1L SymbolKind.Value
              Kind = SemanticEditKind.Insert
              BaselineHash = None
              UpdatedHash = Some 42 }

        let result = diffResult [ edit ] [] |> FSharpDefinitionMap.ofTypedTreeDiff

        let added = FSharpDefinitionMap.added result
        Assert.Single added |> ignore
        Assert.Equal("Module.AddedValue", (List.head added).QualifiedName)

    [<Fact>]
    let ``method body edit classified as update`` () =
        let edit =
            { Symbol = symbol [ "Module" ] "Method" 2L SymbolKind.Value
              Kind = SemanticEditKind.MethodBody
              BaselineHash = Some 11
              UpdatedHash = Some 12 }

        let result = diffResult [ edit ] [] |> FSharpDefinitionMap.ofTypedTreeDiff

        let updated = FSharpDefinitionMap.updated result
        Assert.Single updated |> ignore
        let (symbol, kind) = List.head updated
        Assert.Equal("Module.Method", symbol.QualifiedName)
        Assert.Equal(SemanticEditKind.MethodBody, kind)
        let change =
            result.Changes
            |> List.find (fun change -> change.Symbol.LogicalName = "Method")
        Assert.Equal<int option>(Some 11, change.BaselineHash)
        Assert.Equal<int option>(Some 12, change.UpdatedHash)

    [<Fact>]
    let ``type definition edit captured as update`` () =
        let edit =
            { Symbol = symbol [ "Namespace" ] "Entity" 4L SymbolKind.Entity
              Kind = SemanticEditKind.TypeDefinition
              BaselineHash = Some 5
              UpdatedHash = Some 6 }

        let result = diffResult [ edit ] [] |> FSharpDefinitionMap.ofTypedTreeDiff

        let updated = FSharpDefinitionMap.updated result
        Assert.Single updated |> ignore
        let (symbol, kind) = List.head updated
        Assert.Equal(SymbolKind.Entity, symbol.Kind)
        Assert.Equal(SemanticEditKind.TypeDefinition, kind)

    [<Fact>]
    let ``delete edit captured`` () =
        let edit =
            { Symbol = symbol [ "Module" ] "OldValue" 3L SymbolKind.Value
              Kind = SemanticEditKind.Delete
              BaselineHash = Some 1
              UpdatedHash = None }

        let result = diffResult [ edit ] [] |> FSharpDefinitionMap.ofTypedTreeDiff
        let deleted = FSharpDefinitionMap.deleted result
        Assert.Single deleted |> ignore
        Assert.Equal("Module.OldValue", (List.head deleted).QualifiedName)

    [<Fact>]
    let ``rude edits are preserved`` () =
        let rude =
            { Symbol = Some(symbol [] "Type" 4L SymbolKind.Entity)
              Kind = RudeEditKind.SignatureChange
              Message = "Signature changed" }

        let result = diffResult [] [ rude ] |> FSharpDefinitionMap.ofTypedTreeDiff
        Assert.Single result.RudeEdits |> ignore
        Assert.Equal("Signature changed", (List.head result.RudeEdits).Message)
