namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharp.Compiler
open Xunit

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeDiff

open FSharp.Compiler.Service.Tests.Common

type private DiffTestHarness() =
    let projectDir =
        let dir = Path.Combine(Path.GetTempPath(), "typed-tree-diff-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        dir

    let filePath = Path.Combine(projectDir, "Library.fs")
    let dllPath = Path.Combine(projectDir, "Test.dll")
    let projPath = Path.Combine(projectDir, "Test.fsproj")

    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = false,
            keepAllBackgroundSymbolUses = false,
            enableBackgroundItemKeyStoreAndSemanticClassification = false,
            enablePartialTypeChecking = false,
            captureIdentifiersWhenParsing = false,
            useTransparentCompiler = FSharp.Test.CompilerAssertHelpers.UseTransparentCompiler
        )

    static let typedImplementationFilesProperty =
        typeof<FSharpCheckProjectResults>.GetProperty(
            "TypedImplementationFiles",
            BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)

    let args = mkProjectCommandLineArgs(dllPath, [ filePath ])

    let projectOptions =
        { checker.GetProjectOptionsFromCommandLineArgs(projPath, args) with
            SourceFiles = [| filePath |] }

    member _.Rewrite(source: string) =
        File.WriteAllText(filePath, source)
        Range.setTestSource filePath source

    member _.Compile() =
        checker.InvalidateAll()

        let projectResults =
            checker.ParseAndCheckProject(projectOptions)
            |> Async.RunImmediate

        if projectResults.HasCriticalErrors then
            let errors =
                projectResults.Diagnostics
                |> Array.choose (fun diag -> if diag.Severity = FSharpDiagnosticSeverity.Error then Some diag.Message else None)
            failwithf "Compilation failed: %A" errors

        let tupleItems =
            typedImplementationFilesProperty.GetValue(projectResults)
            |> FSharpValue.GetTupleFields

        let tcGlobals = tupleItems[0] :?> FSharp.Compiler.TcGlobals.TcGlobals
        let implFiles = tupleItems[3] :?> CheckedImplFile list

        let matches (CheckedImplFile(qualifiedNameOfFile = qname)) =
            let text = qname.Text
            String.Equals(text, "Library.fs", StringComparison.Ordinal)
            || String.Equals(text, "Library", StringComparison.Ordinal)
            || String.Equals(text, "Test", StringComparison.Ordinal)

        let implFile =
            match List.tryFind matches implFiles with
            | Some impl -> impl
            | None -> failwithf "Could not locate Library implementation file. Available files: %A" (implFiles |> List.map (fun (CheckedImplFile(qualifiedNameOfFile = qname)) -> qname.Text))

        tcGlobals, implFile

    member _.Diff baseline updated =
        let tcGlobals, baselineImpl = baseline
        let _, updatedImpl = updated
        diffImplementationFile tcGlobals baselineImpl updatedImpl

    interface IDisposable with
        member _.Dispose() =
            try checker.InvalidateAll() with _ -> ()
            try Directory.Delete(projectDir, true) with _ -> ()

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private Sources =
    let moduleHeader = "module Library\n"

    let functionReturning value =
        $"{moduleHeader}let value () = {value}\n"

    let inlineFunction inlineKeyword =
        $"{moduleHeader}let {inlineKeyword}value x = x\n"

    let unionWithFields fieldText =
        $"{moduleHeader}type DU = | Case of {fieldText}\n"

module TypedTreeDiffTests =

    [<Fact>]
    let ``unchanged file produces no edits`` () =
        use harness = new DiffTestHarness()
        harness.Rewrite(Sources.functionReturning "1")
        let baseline = harness.Compile()
        harness.Rewrite(Sources.functionReturning "1")
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        Assert.Empty(result.SemanticEdits)
        Assert.Empty(result.RudeEdits)

    [<Fact>]
    let ``method body update produces semantic edit`` () =
        use harness = new DiffTestHarness()
        harness.Rewrite(Sources.functionReturning "1")
        let baseline = harness.Compile()
        harness.Rewrite(Sources.functionReturning "2")
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        Assert.Single(result.SemanticEdits) |> ignore
        Assert.Empty(result.RudeEdits)
        Assert.Equal(SemanticEditKind.MethodBody, result.SemanticEdits[0].Kind)

    [<Fact>]
    let ``inline annotation change triggers rude edit`` () =
        use harness = new DiffTestHarness()
        harness.Rewrite(Sources.inlineFunction "inline ")
        let baseline = harness.Compile()
        harness.Rewrite(Sources.inlineFunction "")
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        Assert.Empty(result.SemanticEdits)
        Assert.Single(result.RudeEdits) |> ignore
        Assert.Equal(RudeEditKind.InlineChange, result.RudeEdits[0].Kind)

    [<Fact>]
    let ``union layout change triggers rude edit`` () =
        use harness = new DiffTestHarness()
        harness.Rewrite(Sources.unionWithFields "int")
        let baseline = harness.Compile()
        harness.Rewrite(Sources.unionWithFields "int * int")
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        Assert.Empty(result.SemanticEdits)
        Assert.Single(result.RudeEdits) |> ignore
        Assert.Equal(RudeEditKind.TypeLayoutChange, result.RudeEdits[0].Kind)

    [<Fact>]
    let ``generic constraint change triggers rude edit`` () =
        // Test that adding/removing generic constraints is detected as a rude edit
        // (SignatureChange) since constraints affect runtime behavior.
        use harness = new DiffTestHarness()
        let baseline_source = "module Library\nlet identity<'T> (x: 'T) = x\n"
        let updated_source = "module Library\nlet identity<'T when 'T :> System.IDisposable> (x: 'T) = x\n"

        harness.Rewrite(baseline_source)
        let baseline = harness.Compile()
        harness.Rewrite(updated_source)
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        // Should produce a rude edit (signature change) not a semantic edit
        Assert.NotEmpty(result.RudeEdits)
        Assert.Equal(RudeEditKind.SignatureChange, result.RudeEdits[0].Kind)

    [<Fact>]
    let ``mutable field change triggers rude edit`` () =
        // Test that toggling mutable on a field is detected as a type layout change
        // since it affects the runtime representation of the type.
        use harness = new DiffTestHarness()
        let baseline_source = "module Library\ntype MyRecord = { Value: int }\n"
        let updated_source = "module Library\ntype MyRecord = { mutable Value: int }\n"

        harness.Rewrite(baseline_source)
        let baseline = harness.Compile()
        harness.Rewrite(updated_source)
        let updated = harness.Compile()

        let result = harness.Diff baseline updated

        // Should produce a rude edit (type layout change) since mutability affects representation
        Assert.NotEmpty(result.RudeEdits)
        Assert.Equal(RudeEditKind.TypeLayoutChange, result.RudeEdits[0].Kind)
