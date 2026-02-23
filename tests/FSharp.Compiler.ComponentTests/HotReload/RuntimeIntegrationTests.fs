namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Reflection.PortableExecutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Collections.Immutable
open Microsoft.FSharp.Reflection
open Xunit

open FSharp.Compiler
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeDiff
open FSharp.Compiler.Text
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open Internal.Utilities
open FSharp.Test
open FSharp.Test.Utilities
open FSharp.Compiler.Diagnostics
open FSharp.Test
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.IlxDeltaEmitter
open System.Runtime.Loader
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers

[<Collection(nameof NotThreadSafeResourceCollection)>]
module RuntimeIntegrationTests =

    let private typedImplementationFilesProperty =
        typeof<FSharpCheckProjectResults>.GetProperty(
            "TypedImplementationFiles",
            BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public
        )

    let private getTypedAssembly (projectResults: FSharpCheckProjectResults) =
        let tupleItems =
            typedImplementationFilesProperty.GetValue(projectResults)
            |> FSharpValue.GetTupleFields

        let tcGlobals = tupleItems[0] :?> FSharp.Compiler.TcGlobals.TcGlobals
        let implFiles = tupleItems[3] :?> CheckedImplFile list

        tcGlobals,
        implFiles
        |> List.map (fun implFile ->
            { ImplFile = implFile
              OptimizeDuringCodeGen = fun _ expr -> expr })
        |> CheckedAssemblyAfterOptimization

    let private createTempProject () =
        let projectDir = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-tests", System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(projectDir) |> ignore
        let fsPath = Path.Combine(projectDir, "Library.fs")
        let dllPath = Path.Combine(projectDir, "Library.dll")
        projectDir, fsPath, dllPath

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

    let private insertedMethodSource =
        """
namespace Sample

type Type =
    static member GetValue() = 1
    static member GetExtra() = 99
"""

    let private compileProject (checker: FSharpChecker) (fsPath: string) (dllPath: string) (source: string) =
        File.WriteAllText(fsPath, source)

        let projectOptions, _ =
            checker.GetProjectOptionsFromScript(
                fsPath,
                SourceText.ofString source,
                assumeDotNetFramework = false,
                useSdkRefs = true,
                useFsiAuxLib = false
            )
            |> Async.RunImmediate

        let projectOptions =
            { projectOptions with
                SourceFiles = [| fsPath |]
                OtherOptions =
                    projectOptions.OtherOptions
                    |> Array.append
                        [| "--target:library"
                           "--langversion:preview"
                           "--optimize-"
                           "--debug:portable"
                           $"--out:{dllPath}" |] }

        let projectResults =
            checker.ParseAndCheckProject(projectOptions)
            |> Async.RunImmediate

        if projectResults.Diagnostics |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error) then
            failwithf "Compilation failed: %A" projectResults.Diagnostics

        let compileDiagnostics, compileException =
            checker.Compile(Array.append [| "fsc.exe" |] (Array.append projectOptions.OtherOptions [| fsPath |]))
            |> Async.RunImmediate

        let compileErrors =
            compileDiagnostics
            |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

        match compileErrors, compileException with
        | [||], None -> projectResults
        | errs, _ -> failwithf "Compilation produced errors: %A" (errs |> Array.map (fun d -> d.Message))

    let private createBaseline (tcGlobals: FSharp.Compiler.TcGlobals.TcGlobals) (dllPath: string) =
        let pdbPath = Path.ChangeExtension(dllPath, ".pdb")

        let ilModule =
            let options : ILReaderOptions =
                { pdbDirPath = None
                  reduceMemoryUsage = ReduceMemoryFlag.Yes
                  metadataOnly = MetadataOnlyFlag.No
                  tryGetMetadataSnapshot = fun _ -> None }

            use reader = OpenILModuleReader dllPath options
            reader.ILModuleDef

        let writerOptions: FSharp.Compiler.AbstractIL.ILBinaryWriter.options =
            { ilg = tcGlobals.ilg
              outfile = dllPath
              pdbfile = Some pdbPath
              emitTailcalls = false
              deterministic = true
              portablePDB = true
              embeddedPDB = false
              embedAllSource = false
              embedSourceList = []
              allGivenSources = []
              sourceLink = ""
              checksumAlgorithm = FSharp.Compiler.AbstractIL.ILPdbWriter.HashAlgorithm.Sha256
              signer = None
              dumpDebugInfo = false
              referenceAssemblyOnly = false
              referenceAssemblyAttribOpt = None
              referenceAssemblySignatureHash = None
              pathMap = PathMap.empty }

        let assemblyBytes, pdbBytesOpt, tokenMappings, _ =
            FSharp.Compiler.AbstractIL.ILBinaryWriter.WriteILBinaryInMemoryWithArtifacts(writerOptions, ilModule, id)

        // Extract module ID from the PE metadata
        use peReader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()
        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleId = if moduleDef.Mvid.IsNil then System.Guid.NewGuid() else metadataReader.GetGuid(moduleDef.Mvid)

        // Use the SRM-free byte-based APIs
        let metadataSnapshot =
            match HotReloadBaseline.metadataSnapshotFromBytes assemblyBytes with
            | Some snapshot -> snapshot
            | None -> failwith "Failed to parse metadata snapshot from assembly bytes"

        let portablePdbSnapshot = pdbBytesOpt |> Option.map HotReloadPdb.createSnapshot

        let coreBaseline = HotReloadBaseline.create ilModule tokenMappings metadataSnapshot moduleId portablePdbSnapshot
        HotReloadBaseline.attachMetadataHandlesFromBytes assemblyBytes coreBaseline

    [<Fact>]
    let ``EmitDeltaForCompilation produces IL/metadata deltas`` () =
        let checker =
            FSharpChecker.Create(
                keepAssemblyContents = true,
                enableBackgroundItemKeyStoreAndSemanticClassification = false,
                captureIdentifiersWhenParsing = false
            )

        let projectDir, fsPath, dllPath = createTempProject ()

        try
            // Baseline compilation
            let baselineResults = compileProject checker fsPath dllPath baselineSource
            let tcGlobals, baselineImplementation = getTypedAssembly baselineResults
            let baseline = createBaseline tcGlobals dllPath

            let service = FSharpEditAndContinueLanguageService.Instance
            service.EndSession()
            service.StartSession(baseline, baselineImplementation)

            // Updated compilation
            let updatedResults = compileProject checker fsPath dllPath updatedSource
            let updatedTcGlobals, updatedImplementation = getTypedAssembly updatedResults
            let updatedModule =
                let options : ILReaderOptions =
                    { pdbDirPath = None
                      reduceMemoryUsage = ReduceMemoryFlag.Yes
                      metadataOnly = MetadataOnlyFlag.No
                      tryGetMetadataSnapshot = fun _ -> None }

                use reader = OpenILModuleReader dllPath options
                reader.ILModuleDef

            // The build pipeline clears the active session once the new binary is written; rehydrate it
            // with the previously captured baseline before emitting the delta.
            service.StartSession(baseline, baselineImplementation)
            Assert.True(service.IsSessionActive)

            match service.EmitDeltaForCompilation(updatedTcGlobals, updatedImplementation, updatedModule) with
            | Error error -> failwithf "EmitDeltaForCompilation failed: %A" error
            | Ok result ->
                Assert.NotEmpty(result.Delta.Metadata)
                Assert.NotEmpty(result.Delta.IL)
                Assert.NotEmpty(result.Delta.UpdatedMethodTokens)
                let session =
                    match service.TryGetSession() with
                    | ValueSome session -> session
                    | ValueNone -> failwith "Session not found after delta emission."

                Assert.Equal(2, session.CurrentGeneration)
                Assert.True(session.PreviousGenerationId.IsSome)
        finally
            try checker.InvalidateAll() with _ -> ()
            try Directory.Delete(projectDir, true) with _ -> ()

    [<Fact>]
    let ``EmitDeltaForCompilation allows supported method insertion edits`` () =
        let checker =
            FSharpChecker.Create(
                keepAssemblyContents = true,
                enableBackgroundItemKeyStoreAndSemanticClassification = false,
                captureIdentifiersWhenParsing = false
            )

        let projectDir, fsPath, dllPath = createTempProject ()

        try
            let baselineResults = compileProject checker fsPath dllPath baselineSource
            let tcGlobals, baselineImplementation = getTypedAssembly baselineResults
            let baseline = createBaseline tcGlobals dllPath

            let service = FSharpEditAndContinueLanguageService.Instance
            service.EndSession()
            service.StartSession(baseline, baselineImplementation)

            let updatedResults = compileProject checker fsPath dllPath insertedMethodSource
            let updatedTcGlobals, updatedImplementation = getTypedAssembly updatedResults
            let updatedModule =
                let options : ILReaderOptions =
                    { pdbDirPath = None
                      reduceMemoryUsage = ReduceMemoryFlag.Yes
                      metadataOnly = MetadataOnlyFlag.No
                      tryGetMetadataSnapshot = fun _ -> None }

                use reader = OpenILModuleReader dllPath options
                reader.ILModuleDef

            // The build pipeline may clear session state during writes; restore the baseline snapshot before emit.
            service.StartSession(baseline, baselineImplementation)

            match service.EmitDeltaForCompilation(updatedTcGlobals, updatedImplementation, updatedModule) with
            | Error error -> failwithf "EmitDeltaForCompilation failed for method insertion: %A" error
            | Ok result ->
                let hasMethodAdd =
                    result.Delta.EncLog
                    |> Array.exists (fun (table, _, op) ->
                        table = FSharp.Compiler.AbstractIL.BinaryConstants.TableNames.Method
                        && op = FSharp.Compiler.AbstractIL.ILDeltaHandles.EditAndContinueOperation.AddMethod)

                Assert.True(hasMethodAdd, "Expected MethodDef add operation for inserted method.")

                match result.Delta.UpdatedBaseline with
                | Some updatedBaseline ->
                    let containsInsertedMethod =
                        updatedBaseline.MethodTokens
                        |> Map.exists (fun key _ -> key.DeclaringType = "Sample.Type" && key.Name = "GetExtra")
                    Assert.True(containsInsertedMethod, "Updated baseline missing inserted method token.")
                | None ->
                    Assert.True(false, "Updated baseline missing after method insertion delta.")
        finally
            try checker.InvalidateAll() with _ -> ()
            try Directory.Delete(projectDir, true) with _ -> ()

    [<Fact>]
    let ``ApplyUpdate succeeds for method body edit`` () =
        // This test requires DOTNET_MODIFIABLE_ASSEMBLIES=debug to be set
        // To run: DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet test --filter "ApplyUpdate succeeds"
        let modifiable = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
        if not (String.Equals(modifiable, "debug", StringComparison.OrdinalIgnoreCase)) then
            printfn "[skip] DOTNET_MODIFIABLE_ASSEMBLIES must be 'debug' for this test"
        else
            // Use the FSharpChecker hot reload API (same as HotReloadDemoApp)
            let checker =
                FSharpChecker.Create(
                    keepAssemblyContents = true,
                    keepAllBackgroundResolutions = false,
                    keepAllBackgroundSymbolUses = false,
                    enableBackgroundItemKeyStoreAndSemanticClassification = false,
                    enablePartialTypeChecking = false,
                    captureIdentifiersWhenParsing = false
                )

            let projectDir = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-applyupdate", System.Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(projectDir) |> ignore
            let fsPath = Path.Combine(projectDir, "Library.fs")
            let dllPath = Path.Combine(projectDir, "Library.dll")
            // Separate runtime copy (matches HotReloadDemoApp pattern)
            let runtimeDllPath = Path.Combine(projectDir, "Library.runtime.dll")

            try
                File.WriteAllText(fsPath, baselineSource)

                // Get project options with hot reload enabled
                let projectOptions, _ =
                    checker.GetProjectOptionsFromScript(
                        fsPath,
                        SourceText.ofString baselineSource,
                        assumeDotNetFramework = false,
                        useSdkRefs = true,
                        useFsiAuxLib = false
                    )
                    |> Async.RunImmediate

                let projectOptions =
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

                // Compile baseline
                checker.InvalidateAll()
                let compileDiagnostics, _ =
                    checker.Compile(Array.concat [ [| "fsc.exe" |]; projectOptions.OtherOptions; projectOptions.SourceFiles ])
                    |> Async.RunImmediate

                let errors = compileDiagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                if errors.Length > 0 then failwithf "Baseline compilation failed: %A" (errors |> Array.map (fun d -> d.Message))

                // Start hot reload session
                match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
                | Error error -> failwithf "Failed to start hot reload session: %A" error
                | Ok () -> ()

                // Copy baseline to runtime location and load it (same as HotReloadDemoApp)
                File.Copy(dllPath, runtimeDllPath, true)
                let pdbPath = Path.ChangeExtension(dllPath, ".pdb")
                if File.Exists(pdbPath) then
                    File.Copy(pdbPath, Path.ChangeExtension(runtimeDllPath, ".pdb"), true)

                let assembly = Assembly.LoadFrom(runtimeDllPath)

                // Verify baseline method value
                let methodType = assembly.GetType("Sample.Type", throwOnError = true)
                let method = methodType.GetMethod("GetValue", BindingFlags.Public ||| BindingFlags.Static)
                let beforeValue = method.Invoke(null, [||]) :?> int
                Assert.Equal(1, beforeValue)

                // Update source
                File.WriteAllText(fsPath, updatedSource)
                checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate

                // Recompile without hot reload capture (same as HotReloadDemoApp pattern)
                let updatedOptions =
                    { projectOptions with
                        OtherOptions =
                            projectOptions.OtherOptions
                            |> Array.filter (fun opt ->
                                not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase))) }

                let compileDiagnostics2, _ =
                    checker.Compile(Array.concat [ [| "fsc.exe" |]; updatedOptions.OtherOptions; updatedOptions.SourceFiles ])
                    |> Async.RunImmediate

                let errors2 = compileDiagnostics2 |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                if errors2.Length > 0 then failwithf "Update compilation failed: %A" (errors2 |> Array.map (fun d -> d.Message))

                // Emit delta
                match checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate with
                | Error error -> failwithf "EmitHotReloadDelta failed: %A" error
                | Ok delta ->
                    Assert.NotEmpty(delta.Metadata)
                    Assert.NotEmpty(delta.IL)

                    let pdbBytes = delta.Pdb |> Option.defaultValue Array.empty

                    printfn "[applyupdate-test] Applying delta: metadata=%d IL=%d PDB=%d" delta.Metadata.Length delta.IL.Length pdbBytes.Length

                    // Apply the delta
                    try
                        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), pdbBytes.AsSpan())
                        printfn "[applyupdate-test] ApplyUpdate succeeded!"
                    with
                    | :? InvalidOperationException as ex when ex.Message.Contains("not editable") ->
                        failwithf "Assembly is NOT EnC-capable: %s" ex.Message
                    | :? InvalidOperationException as ex ->
                        failwithf "ApplyUpdate failed (delta rejected): %s" ex.Message

                    // Verify updated method value
                    let afterValue = method.Invoke(null, [||]) :?> int
                    Assert.Equal(2, afterValue)
                    printfn "[applyupdate-test] SUCCESS: value changed from %d to %d" beforeValue afterValue

            finally
                try checker.EndHotReloadSession() with _ -> ()
                try checker.InvalidateAll() with _ -> ()
                try Directory.Delete(projectDir, true) with _ -> ()

    let private stringLiteralBaselineSource =
        """
namespace Sample

type Type =
    static member GetMessage() = "Hello from generation 0"
"""

    let private stringLiteralUpdatedSource (gen: int) : string =
        $"""
namespace Sample

type Type =
    static member GetMessage() = "Hello from generation {gen}"
"""

    let private applySingleStringUpdateAndAssertRuntimeResult
        (testLabel: string)
        (baselineSource: string)
        (updatedSource: string)
        (baselineExpected: string)
        (updatedExpected: string)
        =
        // These runtime assertions require EnC-capable runtime loading.
        let modifiable = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
        if not (String.Equals(modifiable, "debug", StringComparison.OrdinalIgnoreCase)) then
            printfn "[skip] DOTNET_MODIFIABLE_ASSEMBLIES must be 'debug' for '%s'" testLabel
        else
            let checker =
                FSharpChecker.Create(
                    keepAssemblyContents = true,
                    keepAllBackgroundResolutions = false,
                    keepAllBackgroundSymbolUses = false,
                    enableBackgroundItemKeyStoreAndSemanticClassification = false,
                    enablePartialTypeChecking = false,
                    captureIdentifiersWhenParsing = false
                )

            let projectDir = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-runtime-string-update", System.Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(projectDir) |> ignore
            let fsPath = Path.Combine(projectDir, "Library.fs")
            let dllPath = Path.Combine(projectDir, "Library.dll")
            let runtimeDllPath = Path.Combine(projectDir, "Library.runtime.dll")
            let loadContext = new AssemblyLoadContext($"fsharp-hotreload-runtime-{System.Guid.NewGuid():N}", isCollectible = true)

            try
                File.WriteAllText(fsPath, baselineSource)

                let projectOptions, _ =
                    checker.GetProjectOptionsFromScript(
                        fsPath,
                        SourceText.ofString baselineSource,
                        assumeDotNetFramework = false,
                        useSdkRefs = true,
                        useFsiAuxLib = false
                    )
                    |> Async.RunImmediate

                let projectOptions =
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

                checker.InvalidateAll()

                let baselineCompileDiagnostics, _ =
                    checker.Compile(Array.concat [ [| "fsc.exe" |]; projectOptions.OtherOptions; projectOptions.SourceFiles ])
                    |> Async.RunImmediate

                let baselineErrors =
                    baselineCompileDiagnostics
                    |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

                if baselineErrors.Length > 0 then
                    failwithf "[%s] baseline compilation failed: %A" testLabel (baselineErrors |> Array.map (fun d -> d.Message))

                match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
                | Error error -> failwithf "[%s] failed to start hot reload session: %A" testLabel error
                | Ok () -> ()

                File.Copy(dllPath, runtimeDllPath, true)
                let pdbPath = Path.ChangeExtension(dllPath, ".pdb")
                if File.Exists(pdbPath) then
                    File.Copy(pdbPath, Path.ChangeExtension(runtimeDllPath, ".pdb"), true)

                let assembly = loadContext.LoadFromAssemblyPath(runtimeDllPath)
                let methodType = assembly.GetType("Sample.Type", throwOnError = true)
                let method = methodType.GetMethod("GetMessage", BindingFlags.Public ||| BindingFlags.Static)

                let baselineMessage = method.Invoke(null, [||]) :?> string
                Assert.Equal(baselineExpected, baselineMessage)

                File.WriteAllText(fsPath, updatedSource)
                checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate

                let updatedOptions =
                    { projectOptions with
                        OtherOptions =
                            projectOptions.OtherOptions
                            |> Array.filter (fun opt ->
                                not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase))) }

                let updateCompileDiagnostics, _ =
                    checker.Compile(Array.concat [ [| "fsc.exe" |]; updatedOptions.OtherOptions; updatedOptions.SourceFiles ])
                    |> Async.RunImmediate

                let updateErrors =
                    updateCompileDiagnostics
                    |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

                if updateErrors.Length > 0 then
                    failwithf "[%s] updated compilation failed: %A" testLabel (updateErrors |> Array.map (fun d -> d.Message))

                match checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate with
                | Error error -> failwithf "[%s] EmitHotReloadDelta failed: %A" testLabel error
                | Ok delta ->
                    Assert.NotEmpty(delta.Metadata)
                    Assert.NotEmpty(delta.IL)

                    let pdbBytes = delta.Pdb |> Option.defaultValue Array.empty
                    MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), pdbBytes.AsSpan())

                    let updatedMessage = method.Invoke(null, [||]) :?> string
                    Assert.Equal(updatedExpected, updatedMessage)

            finally
                try loadContext.Unload() with _ -> ()
                try checker.EndHotReloadSession() with _ -> ()
                try checker.InvalidateAll() with _ -> ()
                try Directory.Delete(projectDir, true) with _ -> ()

    [<Fact>]
    let ``Computation-expression output shape is preserved across desugaring variants`` () =
        let simpleBuilderBaseline =
            """
namespace Sample

type HtmlBuilder() =
    member _.Yield(text: string) = text
    member _.Combine(a: string, b: string) = a + b
    member _.Delay(f: unit -> string) = f()
    member _.Run(text: string) = text
    member _.Zero() = ""

type Type =
    static member GetMessage() =
        let html = HtmlBuilder()
        html {
            yield "Hello, "
            yield "watch"
        }
"""

        let simpleBuilderUpdated = simpleBuilderBaseline.Replace("Hello, ", "Welcome, ")

        let localLambdaBaseline =
            """
namespace Sample

type HtmlBuilder() =
    member _.Yield(text: string) = text
    member _.Combine(a: string, b: string) = a + b
    member _.Delay(f: unit -> string) = f()
    member _.Run(text: string) = text
    member _.Zero() = ""

type Type =
    static member GetMessage() =
        let html = HtmlBuilder()
        let prefixFactory = fun () -> "Hello, "
        html {
            yield prefixFactory()
            yield "watch"
        }
"""

        let localLambdaUpdated = localLambdaBaseline.Replace("Hello, ", "Welcome, ")

        let asyncBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        async {
            do! Async.Sleep 1
            let prefix = "Hello"
            return prefix + ", watch"
        }
        |> Async.RunSynchronously
"""

        let asyncUpdated = asyncBaseline.Replace("Hello", "Welcome")

        let scenarios =
            [ ("ce-simple", simpleBuilderBaseline, simpleBuilderUpdated)
              ("ce-local-lambda", localLambdaBaseline, localLambdaUpdated)
              ("ce-async", asyncBaseline, asyncUpdated) ]

        for (label, baseline, updated) in scenarios do
            applySingleStringUpdateAndAssertRuntimeResult label baseline updated "Hello, watch" "Welcome, watch"

    [<Fact>]
    let ``Tier1 construct matrix preserves runtime apply for method-body edits`` () =
        let seqBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        seq {
            yield "Hello"
            yield ", "
            yield "watch"
        }
        |> String.concat ""
"""

        let recordBaseline =
            """
namespace Sample

type Greeting = { Prefix: string; Name: string }

type Type =
    static member GetMessage() =
        let greeting = { Prefix = "Hello"; Name = "watch" }
        $"{greeting.Prefix}, {greeting.Name}"
"""

        let unionBaseline =
            """
namespace Sample

type Greeting =
    | Message of string * string

type Type =
    static member GetMessage() =
        let value = Message("Hello", "watch")
        match value with
        | Message(prefix, name) -> $"{prefix}, {name}"
"""

        let structBaseline =
            """
namespace Sample

[<Struct>]
type Greeting =
    { Prefix: string
      Name: string }

type Type =
    static member GetMessage() =
        let greeting = { Prefix = "Hello"; Name = "watch" }
        greeting.Prefix + ", " + greeting.Name
"""

        let recursiveBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        let rec prefix i =
            if i = 0 then "Hello" else prefix (i - 1)

        let rec suffix i =
            if i = 0 then "watch" else suffix (i - 1)

        prefix 1 + ", " + suffix 1
"""

        let scenarios =
            [ ("tier1-seq", seqBaseline)
              ("tier1-record", recordBaseline)
              ("tier1-union", unionBaseline)
              ("tier1-struct", structBaseline)
              ("tier1-recursive", recursiveBaseline) ]

        for (label, baseline) in scenarios do
            let updated = baseline.Replace("Hello", "Welcome")
            applySingleStringUpdateAndAssertRuntimeResult label baseline updated "Hello, watch" "Welcome, watch"

    [<Fact>]
    let ``Tier2 construct matrix preserves runtime apply for method-body edits`` () =
        let anonymousRecordBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        let greeting = {| Prefix = "Hello"; Name = "watch" |}
        greeting.Prefix + ", " + greeting.Name
"""

        let activePatternBaseline =
            """
namespace Sample

module Internal =
    let (|SplitGreeting|) (text: string) = text.Split(',')

type Type =
    static member GetMessage() =
        match "Hello,watch" with
        | Internal.SplitGreeting parts -> parts.[0] + ", " + parts.[1]
"""

        let objectExpressionBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        let provider =
            { new obj() with
                override _.ToString() = "Hello" }

        provider.ToString() + ", watch"
"""

        let loopBaseline =
            """
namespace Sample

type Type =
    static member GetMessage() =
        let parts = ResizeArray<string>()
        for value in [ "Hello"; "watch" ] do
            parts.Add(value)
        parts.[0] + ", " + parts.[1]
"""

        let quotationBaseline =
            """
namespace Sample

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns

type Type =
    static member GetMessage() =
        let prefix = "Hello"
        let quotation = <@ "watch" @>

        let suffix =
            match quotation with
            | Value(value, _) -> value :?> string
            | _ -> "watch"

        prefix + ", " + suffix
"""

        let scenarios =
            [ ("tier2-anon-record", anonymousRecordBaseline)
              ("tier2-active-pattern", activePatternBaseline)
              ("tier2-object-expression", objectExpressionBaseline)
              ("tier2-loop", loopBaseline)
              ("tier2-quotation", quotationBaseline) ]

        for (label, baseline) in scenarios do
            let updated = baseline.Replace("Hello", "Welcome")
            applySingleStringUpdateAndAssertRuntimeResult label baseline updated "Hello, watch" "Welcome, watch"

    [<Fact>]
    let ``Multi-generation user string literals resolve correctly`` () =
        // This test verifies that user string literals are correctly resolved across
        // multiple delta generations. The bug manifests as CJK character corruption
        // at generation 2+ when stream header sizes don't match padded byte arrays.
        // Requires DOTNET_MODIFIABLE_ASSEMBLIES=debug
        let modifiable = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
        if not (String.Equals(modifiable, "debug", StringComparison.OrdinalIgnoreCase)) then
            printfn "[skip] DOTNET_MODIFIABLE_ASSEMBLIES must be 'debug' for this test"
        else
            let checker =
                FSharpChecker.Create(
                    keepAssemblyContents = true,
                    keepAllBackgroundResolutions = false,
                    keepAllBackgroundSymbolUses = false,
                    enableBackgroundItemKeyStoreAndSemanticClassification = false,
                    enablePartialTypeChecking = false,
                    captureIdentifiersWhenParsing = false
                )

            let projectDir = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-multigen-userstring", System.Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(projectDir) |> ignore
            let fsPath = Path.Combine(projectDir, "StringLiteralMultiGen.fs")
            let dllPath = Path.Combine(projectDir, "StringLiteralMultiGen.dll")
            let runtimeDllPath = Path.Combine(projectDir, "StringLiteralMultiGen.runtime.dll")

            try
                File.WriteAllText(fsPath, stringLiteralBaselineSource)

                let projectOptions, _ =
                    checker.GetProjectOptionsFromScript(
                        fsPath,
                        SourceText.ofString stringLiteralBaselineSource,
                        assumeDotNetFramework = false,
                        useSdkRefs = true,
                        useFsiAuxLib = false
                    )
                    |> Async.RunImmediate

                let projectOptions =
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

                // Compile baseline
                checker.InvalidateAll()
                let compileDiagnostics, _ =
                    checker.Compile(Array.concat [ [| "fsc.exe" |]; projectOptions.OtherOptions; projectOptions.SourceFiles ])
                    |> Async.RunImmediate

                let errors = compileDiagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                if errors.Length > 0 then failwithf "Baseline compilation failed: %A" (errors |> Array.map (fun d -> d.Message))

                // Start hot reload session
                match checker.StartHotReloadSession(projectOptions) |> Async.RunImmediate with
                | Error error -> failwithf "Failed to start hot reload session: %A" error
                | Ok () -> ()

                // Copy baseline to runtime location and load it
                File.Copy(dllPath, runtimeDllPath, true)
                let pdbPath = Path.ChangeExtension(dllPath, ".pdb")
                if File.Exists(pdbPath) then
                    File.Copy(pdbPath, Path.ChangeExtension(runtimeDllPath, ".pdb"), true)

                let assembly = Assembly.LoadFrom(runtimeDllPath)
                let methodType = assembly.GetType("Sample.Type", throwOnError = true)
                let method = methodType.GetMethod("GetMessage", BindingFlags.Public ||| BindingFlags.Static)

                // Verify baseline string
                let baselineMessage = method.Invoke(null, [||]) :?> string
                Assert.Equal("Hello from generation 0", baselineMessage)
                printfn "[multigen-userstring] Baseline: %s" baselineMessage

                // Helper to apply a generation delta
                let applyGeneration gen =
                    let newSource = stringLiteralUpdatedSource gen
                    File.WriteAllText(fsPath, newSource)
                    checker.NotifyFileChanged(fsPath, projectOptions) |> Async.RunImmediate

                    // Recompile without hot reload capture
                    let updatedOptions =
                        { projectOptions with
                            OtherOptions =
                                projectOptions.OtherOptions
                                |> Array.filter (fun opt ->
                                    not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase))) }

                    let compileDiagnostics, _ =
                        checker.Compile(Array.concat [ [| "fsc.exe" |]; updatedOptions.OtherOptions; updatedOptions.SourceFiles ])
                        |> Async.RunImmediate

                    let errors = compileDiagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                    if errors.Length > 0 then failwithf "Gen %d compilation failed: %A" gen (errors |> Array.map (fun d -> d.Message))

                    // Emit delta
                    match checker.EmitHotReloadDelta(projectOptions) |> Async.RunImmediate with
                    | Error error -> failwithf "Gen %d EmitHotReloadDelta failed: %A" gen error
                    | Ok delta ->
                        let pdbBytes = delta.Pdb |> Option.defaultValue Array.empty
                        printfn "[multigen-userstring] Gen %d: metadata=%d IL=%d PDB=%d" gen delta.Metadata.Length delta.IL.Length pdbBytes.Length

                        // Apply the delta
                        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata.AsSpan(), delta.IL.AsSpan(), pdbBytes.AsSpan())

                        // Verify the string is correct
                        let message = method.Invoke(null, [||]) :?> string
                        let expectedMessage = sprintf "Hello from generation %d" gen
                        printfn "[multigen-userstring] Gen %d result: %s (expected: %s)" gen message expectedMessage

                        // This assertion will fail at gen 2 if stream header sizes are not aligned
                        Assert.Equal(expectedMessage, message)

                // Apply generations 1, 2, 3 - the bug manifests at generation 2
                applyGeneration 1
                applyGeneration 2
                applyGeneration 3

                printfn "[multigen-userstring] SUCCESS: All 3 generations applied correctly"

            finally
                try checker.EndHotReloadSession() with _ -> ()
                try checker.InvalidateAll() with _ -> ()
                try Directory.Delete(projectDir, true) with _ -> ()
