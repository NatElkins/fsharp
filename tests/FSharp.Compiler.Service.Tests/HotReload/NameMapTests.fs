namespace FSharp.Compiler.Service.Tests.HotReload

open System
open Xunit

open FSharp.Compiler.SynthesizedTypeMaps

module NameMapTests =

    [<Fact>]
    let ``name map replays recorded sequence`` () =
        let map = FSharpSynthesizedTypeMaps()
        map.BeginSession()

        let first = map.GetOrAddName "lambda"
        let second = map.GetOrAddName "lambda"

        map.BeginSession()

        let replayFirst = map.GetOrAddName "lambda"
        let replaySecond = map.GetOrAddName "lambda"

        Assert.Equal(first, replayFirst)
        Assert.Equal(second, replaySecond)

    let private hasLineNumberSuffix (name: string) =
        let atIndex = name.IndexOf('@')
        atIndex >= 0
        && atIndex + 1 < name.Length
        && Char.IsDigit name[atIndex + 1]

    [<Fact>]
    let ``generated names avoid source line suffixes`` () =
        let map = FSharpSynthesizedTypeMaps()
        map.BeginSession()

        let name = map.GetOrAddName "closure"
        let another = map.GetOrAddName "closure"

        Assert.False(hasLineNumberSuffix name, $"Expected '{name}' to avoid line-number suffixes.")
        Assert.False(hasLineNumberSuffix another, $"Expected '{another}' to avoid line-number suffixes.")

    [<Fact>]
    let ``snapshot reload restores recorded names`` () =
        let map = FSharpSynthesizedTypeMaps()
        map.BeginSession()

        let first = map.GetOrAddName "anon"
        let second = map.GetOrAddName "anon"

        let snapshot = map.Snapshot |> Seq.toArray

        let replay = FSharpSynthesizedTypeMaps()
        replay.LoadSnapshot snapshot
        replay.BeginSession()

        let replayFirst = replay.GetOrAddName "anon"
        let replaySecond = replay.GetOrAddName "anon"

        Assert.Equal<string>(first, replayFirst)
        Assert.Equal<string>(second, replaySecond)
