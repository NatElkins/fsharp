#nowarn "57"

namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open Xunit

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FSharp.Test
open FSharp.Test.Utilities

open FSharp.Compiler.Service.Tests.Common

[<Collection(nameof NotThreadSafeResourceCollection)>]
module HotReloadCheckerTests =

    let private baselineSource =
        """
namespace Sample

type Type =
    static member GetValue() = 1
"""

    let private updatedSource =
        """
namespace Sample

type Type =
    static member GetValue() = 2
"""

    let private createChecker () =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = false,
            keepAllBackgroundSymbolUses = false,
            enableBackgroundItemKeyStoreAndSemanticClassification = false,
            enablePartialTypeChecking = false,
            captureIdentifiersWhenParsing = false,
            useTransparentCompiler = CompilerAssertHelpers.UseTransparentCompiler
        )

    let private prepareProjectOptions
        (checker: FSharpChecker)
        (fsPath: string)
        (dllPath: string)
        (source: string)
        =
        let projectOptions, _ =
            checker.GetProjectOptionsFromScript(
                fsPath,
                SourceText.ofString source,
                assumeDotNetFramework = false,
                useSdkRefs = true,
                useFsiAuxLib = false
            )
            |> Async.RunImmediate

        { projectOptions with
            SourceFiles = [| fsPath |]
            OtherOptions =
                projectOptions.OtherOptions
                |> Array.append
                    [| "--target:library"
                       "--langversion:preview"
                       "--optimize-"
                       "--debug:portable"
                       "--deterministic"
                       "--enable:hotreloaddeltas"
                       $"--out:{dllPath}" |] }

    let private compileProject
        (checker: FSharpChecker)
        (projectOptions: FSharpProjectOptions)
        (includeHotReloadCapture: bool)
        =
        let options =
            if includeHotReloadCapture then
                projectOptions.OtherOptions
            else
                projectOptions.OtherOptions
                |> Array.filter (fun opt -> not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)))

        let argv =
            Array.concat [ [| "fsc.exe" |]; options; projectOptions.SourceFiles ]

        let diagnostics, exOpt =
            checker.Compile(argv)
            |> Async.RunImmediate

        let errors =
            diagnostics
            |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

        match errors, exOpt with
        | [||], None -> ()
        | errs, _ ->
            failwithf "Compilation failed: %A" (errs |> Array.map (fun d -> d.Message))

    [<Fact>]
    let ``HotReloadCapabilities expose supported flags`` () =
        let checker = createChecker ()
        let capabilities = checker.HotReloadCapabilities

        Assert.True(capabilities.SupportsIl, "Expected IL support flag to be set")
        Assert.True(capabilities.SupportsMetadata, "Expected metadata support flag to be set")
        Assert.True(capabilities.SupportsPortablePdb, "Expected portable PDB support flag to be set")
        Assert.True(capabilities.SupportsMultipleGenerations, "Expected multi-generation flag to be set")
        Assert.False(capabilities.SupportsRuntimeApply, "Runtime apply capability should require explicit opt-in")

    [<Fact>]
    let ``StartHotReloadSession and EmitHotReloadDelta produce delta`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-checker", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, baselineSource)

        let checker = createChecker ()

        let projectOptions = prepareProjectOptions checker fsPath dllPath baselineSource

        // Build the baseline assembly that StartHotReloadSession will use.
        checker.InvalidateAll()
        compileProject checker projectOptions true

        let startResult =
            checker.StartHotReloadSession(projectOptions)
            |> Async.RunImmediate

        match startResult with
        | Error error -> failwithf "Failed to start hot reload session: %A" error
        | Ok () -> ()

        // Update source, rebuild without triggering another baseline capture, and emit a delta.
        File.WriteAllText(fsPath, updatedSource)
        checker.NotifyFileChanged(fsPath, projectOptions)
        |> Async.RunImmediate
        compileProject checker projectOptions false

        let emitResult =
            checker.EmitHotReloadDelta(projectOptions)
            |> Async.RunImmediate

        match emitResult with
        | Error error -> failwithf "EmitHotReloadDelta failed: %A" error
        | Ok delta ->
            Assert.NotEmpty(delta.Metadata)
            Assert.NotEmpty(delta.IL)
            Assert.NotEmpty(delta.UpdatedMethods)

        checker.EndHotReloadSession()
        Assert.False(checker.HotReloadSessionActive)

        try
            Directory.Delete(projectDir, true)
        with _ -> ()
