namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Text.Json
open Xunit

module RoslynBaselineComparisons =

    type RoslynDeltaTables = { delta: int; rows: Map<string, int> }

    let private loadRoslynTables () =
        let path = Path.Combine(__SOURCE_DIRECTORY__, "../../../../../tools/baselines/roslyn_tables.json") |> Path.GetFullPath
        if not (File.Exists path) then
            failwithf "Roslyn baseline table snapshot not found: %s" path
        JsonSerializer.Deserialize<RoslynDeltaTables list>(File.ReadAllText path, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

    [<Fact>]
    let ``roslyn delta tables include expected Module/Method/Param rows`` () =
        let baselines = loadRoslynTables ()
        Assert.True(baselines.Length >= 2, "Expected at least two Roslyn deltas")
        let delta1 = baselines[0].rows
        Assert.Equal(1, delta1.['Module'])
        Assert.Equal(3, delta1.['MethodDef'])
        Assert.Equal(2, delta1.['Param'])
        let delta2 = baselines[1].rows
        Assert.Equal(1, delta2.['Module'])
        Assert.Equal(2, delta2.['MethodDef'])
