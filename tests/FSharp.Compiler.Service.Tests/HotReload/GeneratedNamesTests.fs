namespace FSharp.Compiler.Service.Tests.HotReload

open Xunit

open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.SynthesizedTypeMaps
open FSharp.Compiler.Text

module GeneratedNamesTests =

    let zeroRange = Range.range0

    [<Fact>]
    let ``NiceNameGenerator without map uses hot reload suffix`` () =
        let generator = NiceNameGenerator(fun () -> None)

        let first = generator.FreshCompilerGeneratedName("lambda", zeroRange)
        let second = generator.FreshCompilerGeneratedName("lambda", zeroRange)

        Assert.Equal("lambda@hotreload", first)
        Assert.Equal("lambda@hotreload-1", second)

    [<Fact>]
    let ``NiceNameGenerator with synthesized map replays snapshot`` () =
        let map = FSharpSynthesizedTypeMaps()
        map.BeginSession()

        let generator = NiceNameGenerator(fun () -> Some map)

        let first = generator.FreshCompilerGeneratedName("closure", zeroRange)
        let second = generator.FreshCompilerGeneratedName("closure", zeroRange)

        let snapshot =
            map.Snapshot
            |> Seq.find (fun (key, _) -> key = "closure")
            |> snd

        map.BeginSession()

        let replayFirst = generator.FreshCompilerGeneratedName("closure", zeroRange)
        let replaySecond = generator.FreshCompilerGeneratedName("closure", zeroRange)

        Assert.Equal<string[]>(snapshot, [| first; second |])
        Assert.Equal<string[]>(snapshot, [| replayFirst; replaySecond |])

    [<Fact>]
    let ``NiceNameGenerator counters not incremented during hot reload mode`` () =
        // This test verifies that when hot reload is enabled, the internal
        // basicNameCounts counter is NOT incremented. This prevents counter drift
        // between the per-file basicNameCounts and the global map ordinals.
        let mutable mapEnabled = true
        let map = FSharpSynthesizedTypeMaps()
        map.BeginSession()

        let generator = NiceNameGenerator(fun () -> if mapEnabled then Some map else None)

        // Generate names while hot reload is enabled
        let _ = generator.FreshCompilerGeneratedName("test", zeroRange)
        let _ = generator.FreshCompilerGeneratedName("test", zeroRange)

        // Disable hot reload - should start ordinals fresh
        mapEnabled <- false

        // Without the fix, these would be "test@hotreload-2" and "test@hotreload-3"
        // because the counter was incorrectly incremented during hot reload mode.
        // With the fix, these start at 0 since the counter wasn't touched.
        let first = generator.FreshCompilerGeneratedName("test", zeroRange)
        let second = generator.FreshCompilerGeneratedName("test", zeroRange)

        Assert.Equal("test@hotreload", first)
        Assert.Equal("test@hotreload-1", second)
