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
