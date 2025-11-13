namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Text.Json
open Xunit
open FSharp.Compiler.Service.Tests.HotReload

module private MetadataHelpers =
    let countRows (metadata: byte[]) (table: TableIndex) =
        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange metadata)
        provider.GetMetadataReader().GetTableRowCount(table)

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

    let private assertMatches (expected: Map<string,int>) (deltaBytes: byte[]) =
        let encLog = MetadataHelpers.countRows deltaBytes TableIndex.EncLog
        let encMap = MetadataHelpers.countRows deltaBytes TableIndex.EncMap
        let methodDef = MetadataHelpers.countRows deltaBytes TableIndex.MethodDef
        Assert.Equal(expected.['EncLog'], encLog)
        Assert.Equal(expected.['EncMap'], encMap)
        Assert.Equal(expected.['MethodDef'], methodDef)

    [<Fact>]
    let ``property delta row counts do not exceed Roslyn baseline`` () =
        let baselines = loadRoslynTables ()
        let roslyn = baselines |> List.tryHead |> Option.defaultWith (fun () -> failwith "Roslyn property baseline missing")

        let propertyDelta = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        assertMatches roslyn.rows propertyDelta.Delta.Metadata

    [<Fact>]
    let ``property multi-generation delta rows match Roslyn baseline`` () =
        let baselines = loadRoslynTables ()
        let roslyn = baselines |> List.tryHead |> Option.defaultWith (fun () -> failwith "Roslyn property baseline missing")
        let roslynRows = roslyn.rows

        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        assertMatches roslynRows artifacts.Generation1.Metadata
        assertMatches roslynRows artifacts.Generation2.Metadata

    [<Fact>]
    let ``event delta row counts match Roslyn baseline`` () =
        let baselines = loadRoslynTables ()
        let roslynEvent = baselines |> List.tryItem 1 |> Option.defaultWith (fun () -> failwith "Roslyn event baseline missing")

        let roslynEncLog = roslynEvent.rows.['EncLog']
        let roslynEncMap = roslynEvent.rows.['EncMap']
        let roslynMethodDef = roslynEvent.rows.['MethodDef']

        let eventDelta = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        let deltaBytes = eventDelta.Delta.Metadata

        let encLog = MetadataHelpers.countRows deltaBytes TableIndex.EncLog
        let encMap = MetadataHelpers.countRows deltaBytes TableIndex.EncMap
        let methodDef = MetadataHelpers.countRows deltaBytes TableIndex.MethodDef

        Assert.Equal(roslynEncLog, encLog)
        Assert.Equal(roslynEncMap, encMap)
        Assert.Equal(roslynMethodDef, methodDef)

    [<Fact>]
    let ``async delta row counts match Roslyn baseline`` () =
        let baselines = loadRoslynTables ()
        let roslynAsync = baselines |> List.tryItem 2 |> Option.defaultWith (fun () -> failwith "Roslyn async baseline missing")

        let roslynEncLog = roslynAsync.rows.['EncLog']
        let roslynEncMap = roslynAsync.rows.['EncMap']
        let roslynMethodDef = roslynAsync.rows.['MethodDef']

        let asyncDelta = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let deltaBytes = asyncDelta.Delta.Metadata

        let encLog = MetadataHelpers.countRows deltaBytes TableIndex.EncLog
        let encMap = MetadataHelpers.countRows deltaBytes TableIndex.EncMap
        let methodDef = MetadataHelpers.countRows deltaBytes TableIndex.MethodDef

        Assert.Equal(roslynEncLog, encLog)
        Assert.Equal(roslynEncMap, encMap)
        Assert.Equal(roslynMethodDef, methodDef)
