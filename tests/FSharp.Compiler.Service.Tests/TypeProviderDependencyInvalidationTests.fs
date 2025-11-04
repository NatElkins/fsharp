#nowarn "57"

namespace FSharp.Compiler.Service.Tests

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Service.Tests.Common
open FSharp.Compiler.Symbols

module TypeProviderDependencyInvalidationTests =

    let private providedTypesAssembly = typeof<ProviderImplementation.ProvidedTypes.ProvidedTypeDefinition>.Assembly.Location

    let private providerSource = """
namespace Fs1023

open System
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes

[<TypeProvider>]
type Fs1023Provider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = Assembly.GetExecutingAssembly()
    let namespaceName = "Fs1023"
    let generator = ProvidedTypeDefinition(assembly, namespaceName, "ProvidedGenerator", Some typeof<obj>, isErased = false)

    do
        let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

        generator.DefineStaticParameters(parameters, fun typeName args ->
            let sourceType = args.[0] :?> Type
            let provided = ProvidedTypeDefinition(assembly, namespaceName, typeName, Some typeof<obj>, isErased = false, hideObjectMethods = true)

            let logMethods label =
                let methods =
                    sourceType.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun m ->
                        let parameters =
                            m.GetParameters()
                            |> Array.map (fun p -> sprintf "%s:%s" p.Name p.ParameterType.Name)
                            |> String.concat ","
                        sprintf "%s(%s)" m.Name parameters)
                printfn "[fs1023][%s][methods] %s" label (String.concat "; " methods)

            let logProperties label =
                let properties =
                    sourceType.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun p ->
                        let indexers =
                            p.GetIndexParameters()
                            |> Array.map (fun idx -> sprintf "%s:%s" idx.Name idx.ParameterType.Name)
                            |> String.concat ","
                        sprintf "%s[%s]:%s" p.Name indexers p.PropertyType.Name)
                printfn "[fs1023][%s][properties] %s" label (String.concat "; " properties)

            logMethods typeName
            logProperties typeName

            let logParameterDetails (m: MethodInfo) =
                let parameters =
                    m.GetParameters()
                    |> Array.map (fun p ->
                        let attrs =
                            p.GetCustomAttributesData()
                            |> Seq.map (fun a -> a.AttributeType.Name)
                            |> String.concat ","
                        sprintf "%s:%s optional=%b default=%b attrs=[%s]" p.Name p.ParameterType.FullName p.IsOptional p.HasDefaultValue attrs)
                    |> String.concat "; "
                printfn "[fs1023][%s][method:%s] %s" typeName m.Name parameters

            let logPropertyDetails (p: PropertyInfo) =
                let indexers =
                    p.GetIndexParameters()
                    |> Array.map (fun idx ->
                        let attrs =
                            idx.GetCustomAttributesData()
                            |> Seq.map (fun a -> a.AttributeType.Name)
                            |> String.concat ","
                        sprintf "%s:%s attrs=[%s]" idx.Name idx.ParameterType.FullName attrs)
                    |> String.concat "; "
                printfn "[fs1023][%s][property:%s] type=%s indexers=%s" typeName p.Name p.PropertyType.FullName indexers

            sourceType.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.iter logParameterDetails

            sourceType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.iter logPropertyDetails

            let addMemberFromProperty (pi: PropertyInfo) =
                if pi.GetIndexParameters().Length = 0 then
                    printfn "[fs1023][addMember] adding property %s" pi.Name
                    let getter (_args: Expr list) =
                        let name = pi.Name
                        <@@ name @@>
                    let property = ProvidedProperty(pi.Name, typeof<string>, isStatic = true, getterCode = getter)
                    provided.AddMember property

            sourceType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.iter addMemberFromProperty

            let mapSummary =
                match sourceType.GetMethod("Map", BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> "missing"
                | m ->
                    m.GetParameters()
                    |> Array.map (fun p ->
                        let optional = if p.IsOptional then "optional" else "required"
                        let isParamArray = if p.IsDefined(typeof<ParamArrayAttribute>, false) then "paramarray" else "normal"
                        sprintf "%s:%s:%s" p.Name optional isParamArray)
                    |> String.concat ";"

            let optionalSummary =
                match sourceType.GetMethod("Optional", BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> "missing"
                | m ->
                    match m.GetParameters() |> Array.tryHead with
                    | None -> "none"
                    | Some p -> sprintf "%s:%b:%b" p.Name p.IsOptional p.HasDefaultValue

            let optionalLiteralSummary =
                match sourceType.GetMethod("OptionalLiteral", BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> "missing"
                | m ->
                    match m.GetParameters() |> Array.tryHead with
                    | None -> "none"
                    | Some p ->
                        let raw =
                            let value = p.RawDefaultValue
                            if obj.ReferenceEquals(value, Type.Missing) then "missing"
                            elif isNull value then "null"
                            else value.ToString()
                        sprintf "%s:%b:%b:%s" p.Name p.IsOptional p.HasDefaultValue raw

            let indexerSummary =
                match sourceType.GetProperty("Item", BindingFlags.Public ||| BindingFlags.Instance, null, typeof<int>, [| typeof<int> |], null) with
                | null -> "missing"
                | prop ->
                    prop.GetIndexParameters()
                    |> Array.map (fun p -> sprintf "%s:%s" p.Name p.ParameterType.Name)
                    |> String.concat ";"

            printfn "[fs1023][%s][summary] Map=%s Optional=%s OptionalLiteral=%s Indexer=%s" typeName mapSummary optionalSummary optionalLiteralSummary indexerSummary

            let addSummaryProperty name value =
                let getter (_: Expr list) = Expr.Value value
                let property = ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = getter)
                provided.AddMember property

            addSummaryProperty "MapParameters" mapSummary
            addSummaryProperty "OptionalParameter" optionalSummary
            addSummaryProperty "OptionalLiteralParameter" optionalLiteralSummary
            addSummaryProperty "IndexerParameters" indexerSummary

            provided)

    do this.AddNamespace(namespaceName, [ generator ])

[<assembly: TypeProviderAssembly>]
do ()
"""

    let private modelSourceInitial = """
namespace Fs1023Consumer

open System

type Model =
    { Value: int }
    with
        member _.Map(value: int, [<ParamArray>] rest: string[]) = value + rest.Length
        member _.Optional(?value: int) = defaultArg value 0
        member _.Item
            with get(index: int) = index
        member _.OptionalLiteral([<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(42)>] value: int) =
            value
"""

    let private modelSourceRenamed = """
namespace Fs1023Consumer

open System

type Model =
    { Renamed: int }
    with
        member _.Map(value: int, [<ParamArray>] rest: string[]) = value + rest.Length
        member _.Optional(?value: int) = defaultArg value 0
        member _.Item
            with get(index: int) = index
        member _.OptionalLiteral([<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(42)>] value: int) =
            value
"""

    let private consumerSource = """
namespace Fs1023Consumer

type Provided = Fs1023.ProvidedGenerator<Source = Fs1023Consumer.Model>

module UseProvided =
    let valueName = Provided.Value
"""

    let private consumerSourceAnonymousRecord = """
namespace Fs1023Consumer

type Provided = Fs1023.ProvidedGenerator<Source = {| Value: int |}>
"""

    let private consumerSourceTypeParameter = """
namespace Fs1023Consumer

type Provided<'T> = Fs1023.ProvidedGenerator<Source = 'T>
"""

    let private consumerSourceProvidedType = """
namespace Fs1023Consumer

type Model = { Value: int }

type ProvidedGenerated = Fs1023.ProvidedGenerator<Source = Model>
type ProvidedProvided = Fs1023.ProvidedGenerator<Source = ProvidedGenerated>
"""

    let private ensureSuccess (diagnostics: FSharpDiagnostic[]) =
        diagnostics
        |> Array.iter (fun d ->
            if d.Severity = FSharpDiagnosticSeverity.Error then
                printfn "[fs1023][diagnostic] %s" d.Message
            if d.Severity = FSharpDiagnosticSeverity.Error then
                failwithf "Compilation error (%s:%d,%d): %s" d.FileName d.StartLine d.StartColumn d.Message)

    let private compile args =
        let diagnostics, exnOpt = checker.Compile args |> Async.RunImmediate
        ensureSuccess diagnostics

        match exnOpt with
        | Some ex -> raise ex
        | None -> ()

    let private writeFile (path: string) (contents: string) =
        let directory = Path.GetDirectoryName(path)

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory(directory) |> ignore

        File.WriteAllText(path, contents)

    let private getFullPath path = Path.GetFullPath path

    [<Fact>]
    let ``type provider re-runs when source type changes`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSource

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| modelPath; consumerPath |] }

            let checkProject () = checker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            let initialResults = checkProject()

            ensureSuccess initialResults.Diagnostics

            let dependencyFiles =
                initialResults.DependencyFiles
                |> Array.map getFullPath

            Assert.Contains(modelPath |> getFullPath, dependencyFiles)

            // Modify the source type without touching the consumer
            writeFile modelPath modelSourceRenamed
            checker.NotifyFileChanged(modelPath, projectOptions) |> Async.RunImmediate

            let updatedResults = checkProject()

            let errors =
                updatedResults.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            errors |> Array.iter (fun d -> printfn "[rerun] %s" d.Message)

            Assert.True(errors.Length > 0, "Expected type provider to invalidate generated members after source change.")
            Assert.True(errors |> Array.exists (fun e -> e.Message.Contains "Value"), "Expected missing member error referencing 'Value'.")
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``anonymous record static argument is rejected`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile consumerPath consumerSourceAnonymousRecord

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| consumerPath |] }

            let results = checker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            let errors =
                results.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            Assert.True(errors.Length > 0, "Expected anonymous record static argument to be rejected.")
            Assert.True(errors |> Array.exists (fun e -> e.Message.Contains "anonymous record types are not supported as static arguments"), "Expected diagnostic mentioning anonymous record support.")
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``type parameter static argument is rejected`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSourceTypeParameter

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| modelPath; consumerPath |] }

            let results = checker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            let errors =
                results.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            errors |> Array.iter (fun d -> printfn "[typeparam] %s" d.Message)

            Assert.True(errors.Length > 0, "Expected type parameter static argument to be rejected.")
            Assert.True(errors |> Array.exists (fun e -> e.Message.Contains "type parameters are not supported as static arguments"), "Expected diagnostic mentioning type parameter restriction.")
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``provided type static argument is rejected`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let consumerPath = Path.Combine(tempDir, "Consumer.fs")
            writeFile consumerPath consumerSourceProvidedType

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| consumerPath |] }

            let results = checker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            let errors =
                results.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            errors |> Array.iter (fun d -> printfn "[provided] %s" d.Message)

            Assert.True(errors.Length > 0, "Expected provided type static argument to be rejected.")
            Assert.True(errors |> Array.exists (fun e -> e.Message.Contains "provided types cannot be used as static arguments"), "Expected diagnostic mentioning provided type restriction.")
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``reflection proxy surfaces parameter metadata`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let loadWithContext (path: string) =
            let context = new AssemblyLoadContext("fs1023-reflection-" + Guid.NewGuid().ToString("N"), true)
            context.add_Resolving(fun ctx name ->
                let candidate = Path.Combine(tempDir, name.Name + ".dll")
                if File.Exists(candidate) then ctx.LoadFromAssemblyPath(candidate) else null)
            context.LoadFromAssemblyPath(path), context

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSource

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            compile projectArgs

            let assembly, context = loadWithContext outputDll

            try
                let providedType = assembly.GetType("Fs1023Consumer.Provided", throwOnError = true)

                let readStaticProperty name =
                    let property = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(property)
                    property.GetValue(null, null) :?> string

                let mapSummary = readStaticProperty "MapParameters"
                let optionalSummary = readStaticProperty "OptionalParameter"
                let optionalLiteralSummary = readStaticProperty "OptionalLiteralParameter"
                let indexerSummary = readStaticProperty "IndexerParameters"

                printfn "[fs1023][assert] MapParameters=%s" mapSummary
                printfn "[fs1023][assert] OptionalParameter=%s" optionalSummary
                printfn "[fs1023][assert] OptionalLiteralParameter=%s" optionalLiteralSummary
                printfn "[fs1023][assert] IndexerParameters=%s" indexerSummary

                Assert.Equal("value:required:normal;rest:required:paramarray", mapSummary)
                Assert.Equal("value:true:true", optionalSummary)
                Assert.Equal("value:true:true:42", optionalLiteralSummary)
                Assert.Equal("index:Int32", indexerSummary)
            finally
                context.Unload()
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact(Skip = "FS-1023: Generative members are not yet published into the TAST")>]
    let ``provided type publishes members into the TAST`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSource

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| modelPath; consumerPath |] }

            let checkerWithContents =
                FSharpChecker.Create(
                    keepAssemblyContents = true,
                    useTransparentCompiler = FSharp.Test.CompilerAssertHelpers.UseTransparentCompiler)

            let projectResults = checkerWithContents.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            ensureSuccess projectResults.Diagnostics

            let providedEntity =
                projectResults.AssemblyContents.ImplementationFiles
                |> Seq.collect (fun impl ->
                    impl.Declarations
                    |> Seq.choose (function
                        | FSharpImplementationFileDeclaration.Entity(entity, _) when
                            entity.FullName = "Fs1023Consumer.Provided" ->
                            Some entity
                        | _ -> None))
                |> Seq.tryHead

            Assert.True(providedEntity.IsSome, "Expected Fs1023Consumer.Provided to be present in the typed tree.")

            let memberNames =
                providedEntity.Value.MembersFunctionsAndValues
                |> Seq.map (fun mfv -> mfv.CompiledName)
                |> Seq.toArray

            Assert.Contains("get_Value", memberNames)
            Assert.Contains("MapParameters", memberNames)
        finally
            try Directory.Delete(tempDir, true) with _ -> ()
