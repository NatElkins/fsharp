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

    let private withShortOutputOption (projectOptions: FSharpProjectOptions) (dllPath: string) =
        { projectOptions with
            OtherOptions =
                projectOptions.OtherOptions
                |> Array.filter (fun opt ->
                    not (opt.StartsWith("--out:", StringComparison.OrdinalIgnoreCase) ||
                         opt.StartsWith("-o:", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(opt, "-o", StringComparison.OrdinalIgnoreCase)))
                |> Array.append [| $"-o:{dllPath}" |] }

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

    [<Fact>]
    let ``StartHotReloadSession accepts short output option`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-short-output", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, baselineSource)

        let checker = createChecker ()
        let baselineOptions = prepareProjectOptions checker fsPath dllPath baselineSource
        let projectOptions = withShortOutputOption baselineOptions dllPath

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start hot reload session with -o: output option: %A" error
        | Ok () -> ()

        File.WriteAllText(fsPath, updatedSource)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate
        compileProject checker projectOptions false

        match checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate with
        | Ok delta ->
            Assert.NotEmpty(delta.Metadata)
            Assert.NotEmpty(delta.IL)
        | Error FSharpHotReloadError.MissingOutputPath ->
            failwith "Expected -o: output option to resolve to a valid output path."
        | Error error ->
            failwithf "EmitHotReloadDelta failed for -o: output option: %A" error

        checker.EndHotReloadSession()

        try
            Directory.Delete(projectDir, true)
        with _ -> ()

    [<Fact>]
    let ``EmitHotReloadDelta rejects stale output assembly`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-stale-output", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, baselineSource)

        let checker = createChecker ()
        let projectOptions = prepareProjectOptions checker fsPath dllPath baselineSource

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start session: %A" error
        | Ok () -> ()

        File.WriteAllText(fsPath, updatedSource)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate

        // Intentionally skip recompilation so the output assembly stays stale.
        match checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate with
        | Error (FSharpHotReloadError.DeltaEmissionFailed message) ->
            Assert.Contains("stale build output", message, StringComparison.OrdinalIgnoreCase)
        | Error other -> failwithf "Expected DeltaEmissionFailed for stale output, got %A" other
        | Ok _ -> failwith "Expected stale output detection to reject delta emission."

        checker.EndHotReloadSession()

        try
            Directory.Delete(projectDir, true)
        with _ -> ()

    // -------------------------------------------------------------------------
    // Rude Edit Rejection Tests
    // -------------------------------------------------------------------------
    // These tests verify that disallowed edits are properly rejected at the
    // FSharpChecker API level, returning UnsupportedEdit errors.

    let private signatureChangeBaseline =
        """
namespace Sample

type Type =
    static member GetValue(x: int) = x + 1
"""

    let private signatureChangeUpdated =
        """
namespace Sample

type Type =
    static member GetValue(x: string) = x.Length
"""

    [<Fact>]
    let ``EmitHotReloadDelta rejects signature change`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-sig-change", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, signatureChangeBaseline)

        let checker = createChecker ()
        let projectOptions = prepareProjectOptions checker fsPath dllPath signatureChangeBaseline

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start session: %A" error
        | Ok () -> ()

        // Change the method signature (int -> string parameter)
        File.WriteAllText(fsPath, signatureChangeUpdated)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate
        compileProject checker projectOptions false

        let emitResult = checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate

        match emitResult with
        | Ok _ -> failwith "Expected signature change to be rejected"
        | Error (FSharpHotReloadError.UnsupportedEdit msg) ->
            Assert.Contains("Rude edits", msg, StringComparison.OrdinalIgnoreCase)
        | Error other -> failwithf "Expected UnsupportedEdit error, got: %A" other

        checker.EndHotReloadSession()
        try Directory.Delete(projectDir, true) with _ -> ()

    let private recordBaseline =
        """
namespace Sample

type Person = { Name: string }

module Helpers =
    let greet (p: Person) = $"Hello, {p.Name}"
"""

    let private recordWithNewField =
        """
namespace Sample

type Person = { Name: string; Age: int }

module Helpers =
    let greet (p: Person) = $"Hello, {p.Name}, age {p.Age}"
"""

    [<Fact>]
    let ``EmitHotReloadDelta rejects record field addition`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-record-field", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, recordBaseline)

        let checker = createChecker ()
        let projectOptions = prepareProjectOptions checker fsPath dllPath recordBaseline

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start session: %A" error
        | Ok () -> ()

        // Add a new field to the record (type layout change)
        File.WriteAllText(fsPath, recordWithNewField)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate
        compileProject checker projectOptions false

        let emitResult = checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate

        match emitResult with
        | Ok _ -> failwith "Expected record field addition to be rejected"
        | Error (FSharpHotReloadError.UnsupportedEdit msg) ->
            // Should mention rude edits or structural edits
            Assert.True(
                msg.Contains("Rude", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Structural", StringComparison.OrdinalIgnoreCase),
                $"Expected rude/structural edit message, got: {msg}")
        | Error other -> failwithf "Expected UnsupportedEdit error, got: %A" other

        checker.EndHotReloadSession()
        try Directory.Delete(projectDir, true) with _ -> ()

    let private moduleBaseline =
        """
namespace Sample

module Helpers =
    let getValue () = 42
"""

    let private moduleWithNewFunction =
        """
namespace Sample

module Helpers =
    let getValue () = 42
    let getOther () = 99
"""

    [<Fact>]
    let ``EmitHotReloadDelta rejects new function addition`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-func-add", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, moduleBaseline)

        let checker = createChecker ()
        let projectOptions = prepareProjectOptions checker fsPath dllPath moduleBaseline

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start session: %A" error
        | Ok () -> ()

        // Add a new function (declaration added)
        File.WriteAllText(fsPath, moduleWithNewFunction)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate
        compileProject checker projectOptions false

        let emitResult = checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate

        match emitResult with
        | Ok _ -> failwith "Expected new function addition to be rejected"
        | Error (FSharpHotReloadError.UnsupportedEdit msg) ->
            // Should mention rude edits or structural edits
            Assert.True(
                msg.Contains("Rude", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Structural", StringComparison.OrdinalIgnoreCase),
                $"Expected rude/structural edit message, got: {msg}")
        | Error other -> failwithf "Expected UnsupportedEdit error, got: %A" other

        checker.EndHotReloadSession()
        try Directory.Delete(projectDir, true) with _ -> ()

    let private unionBaseline =
        """
namespace Sample

type Shape =
    | Circle of radius: float
    | Square of side: float

module Shapes =
    let area shape =
        match shape with
        | Circle r -> System.Math.PI * r * r
        | Square s -> s * s
"""

    let private unionWithNewCase =
        """
namespace Sample

type Shape =
    | Circle of radius: float
    | Square of side: float
    | Triangle of base': float * height: float

module Shapes =
    let area shape =
        match shape with
        | Circle r -> System.Math.PI * r * r
        | Square s -> s * s
        | Triangle (b, h) -> 0.5 * b * h
"""

    [<Fact>]
    let ``EmitHotReloadDelta rejects union case addition`` () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fcs-hotreload-union-case", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore

        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")

        File.WriteAllText(fsPath, unionBaseline)

        let checker = createChecker ()
        let projectOptions = prepareProjectOptions checker fsPath dllPath unionBaseline

        checker.InvalidateAll()
        compileProject checker projectOptions true

        match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
        | Error error -> failwithf "Failed to start session: %A" error
        | Ok () -> ()

        // Add a new union case (type layout change)
        File.WriteAllText(fsPath, unionWithNewCase)
        checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate
        compileProject checker projectOptions false

        let emitResult = checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate

        match emitResult with
        | Ok _ -> failwith "Expected union case addition to be rejected"
        | Error (FSharpHotReloadError.UnsupportedEdit msg) ->
            // Should mention rude edits or structural edits
            Assert.True(
                msg.Contains("Rude", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Structural", StringComparison.OrdinalIgnoreCase),
                $"Expected rude/structural edit message, got: {msg}")
        | Error other -> failwithf "Expected UnsupportedEdit error, got: %A" other

        checker.EndHotReloadSession()
        try Directory.Delete(projectDir, true) with _ -> ()
