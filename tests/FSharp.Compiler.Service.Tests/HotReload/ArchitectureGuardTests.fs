module FSharp.Compiler.Service.Tests.HotReload.ArchitectureGuardTests

open System.IO
open Xunit

let private repoRoot =
    Path.Combine(__SOURCE_DIRECTORY__, "../../..") |> Path.GetFullPath

let private readCompilerFile relativePath =
    Path.Combine(repoRoot, relativePath) |> File.ReadAllText

[<Fact>]
let ``fsc does not directly depend on hot reload implementation modules`` () =
    let source = readCompilerFile "src/Compiler/Driver/fsc.fs"

    Assert.DoesNotContain("open FSharp.Compiler.HotReload\n", source)
    Assert.DoesNotContain("open FSharp.Compiler.HotReloadBaseline\n", source)
    Assert.DoesNotContain("open FSharp.Compiler.HotReloadPdb\n", source)
    Assert.DoesNotContain("open FSharp.Compiler.HotReloadEmitHook\n", source)

[<Fact>]
let ``compiler global state only depends on generated-name abstraction`` () =
    let source = readCompilerFile "src/Compiler/TypedTree/CompilerGlobalState.fs"

    Assert.DoesNotContain("open FSharp.Compiler.SynthesizedTypeMaps\n", source)
    Assert.Contains("open FSharp.Compiler.GeneratedNames\n", source)
    Assert.Contains("member _.CompilerGeneratedNameMap", source)

[<Fact>]
let ``compiler config exposes generic emit hook contract only`` () =
    let source = readCompilerFile "src/Compiler/Driver/CompilerConfig.fsi"

    Assert.DoesNotContain("IHotReloadEmitHook", source)
    Assert.DoesNotContain("HotReloadEmitArtifacts", source)
    Assert.Contains("type ICompilerEmitHook", source)
    Assert.Contains("val defaultCompilerEmitHook", source)

[<Fact>]
let ``compiler emit hook bootstrap remains explicit-only`` () =
    let source = readCompilerFile "src/Compiler/Driver/CompilerEmitHookBootstrap.fs"

    Assert.Contains("tcConfigB.compilerEmitHook <- Some hotReloadCompilerEmitHook", source)
    Assert.DoesNotContain("setAmbientCompilerEmitHook", source)

[<Fact>]
let ``hot reload service owns ambient emit hook lifecycle`` () =
    let source = readCompilerFile "src/Compiler/Service/service.fs"

    Assert.Contains("setAmbientCompilerEmitHook hotReloadCompilerEmitHook", source)
    Assert.Contains("clearAmbientCompilerEmitHook()", source)

let private sliceBetween (source: string) (startMarker: string) (endMarker: string) =
    let startIndex = source.IndexOf(startMarker, System.StringComparison.Ordinal)
    Assert.True(startIndex >= 0, $"Could not find marker '{startMarker}'.")

    let endIndex = source.IndexOf(endMarker, startIndex, System.StringComparison.Ordinal)
    Assert.True(endIndex > startIndex, $"Could not find end marker '{endMarker}' after '{startMarker}'.")

    source.Substring(startIndex, endIndex - startIndex)

[<Fact>]
let ``typed tree diff opDigest stays wildcard free`` () =
    let source = readCompilerFile "src/Compiler/TypedTree/TypedTreeDiff.fs"
    let opDigestSource = sliceBetween source "let private opDigest" "type private LoweredShapeCollector"

    Assert.DoesNotContain("| _ ->", opDigestSource)

[<Fact>]
let ``typed tree diff no longer relies on state-machine declaring-type string heuristic`` () =
    let source = readCompilerFile "src/Compiler/TypedTree/TypedTreeDiff.fs"

    Assert.DoesNotContain("isLikelyStateMachineDeclaringType", source)
    Assert.DoesNotContain("\"AsyncBuilder\"", source)
    Assert.DoesNotContain("\"TaskBuilder\"", source)
    Assert.DoesNotContain("\"Resumable\"", source)
    Assert.DoesNotContain("\"QueryBuilder\"", source)
