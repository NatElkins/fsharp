namespace FSharp.Compiler.Service.Tests.HotReload

open Xunit

open FSharp.Compiler.HotReloadNameMap

module NameMapTests =

    [<Fact>]
    let ``name map replays recorded sequence`` () =
        let map = HotReloadNameMap()
        map.BeginSession()

        let first = map.GetOrAddName "lambda"
        let second = map.GetOrAddName "lambda"

        map.BeginSession()

        let replayFirst = map.GetOrAddName "lambda"
        let replaySecond = map.GetOrAddName "lambda"

        Assert.Equal(first, replayFirst)
        Assert.Equal(second, replaySecond)

    [<Fact>]
    let ``generated names avoid source line suffixes`` () =
        let map = HotReloadNameMap()
        map.BeginSession()

        let name = map.GetOrAddName "closure"
        let another = map.GetOrAddName "closure"

        Assert.DoesNotContain("@", name)
        Assert.DoesNotContain("@", another)
