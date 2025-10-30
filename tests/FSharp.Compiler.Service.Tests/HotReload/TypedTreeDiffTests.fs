namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharp.Compiler
open Xunit

open FSharp.Compiler.CodeAnalysis
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

        let tupleItems =
            typedImplementationFilesProperty.GetValue(projectResults)
            |> FSharpValue.GetTupleFields

        let tcGlobals = tupleItems[0] :?> FSharp.Compiler.TcGlobals.TcGlobals
        let implFiles = tupleItems[3] :?> CheckedImplFile list

        tcGlobals,
        implFiles
        |> List.find (fun (CheckedImplFile(qualifiedNameOfFile = qname)) -> String.Equals(qname.Text, "Library.fs", StringComparison.Ordinal))

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
