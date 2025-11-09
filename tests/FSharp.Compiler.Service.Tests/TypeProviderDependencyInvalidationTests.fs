#nowarn "57"

namespace FSharp.Compiler.Service.Tests

open System
open System.IO
open System.Reflection
open System.Diagnostics
open System.Runtime.Loader
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Service.Tests.Common
open FSharp.Compiler.Symbols

module TypeProviderDependencyInvalidationTests =

    [<Fact>]
    let ``provided binding defaults to None`` () =
        let optData = FSharp.Compiler.TypedTree.Val.NewEmptyValOptData()
#if !NO_TYPEPROVIDERS
        Assert.True(optData.val_provided_binding.IsNone)
#else
        Assert.True(true)
#endif


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

    let private genericModelSource = """
namespace Fs1023Consumer

type Generic<'T> =
    { Value: 'T }
    with
        member _.Map(value: 'T, [<ParamArray>] rest: string[]) =
            ignore rest
            value
        member _.Optional(?value: 'T) = defaultArg value Unchecked.defaultof<'T>
        member _.Item
            with get(index: int) = index
        member _.OptionalLiteral([<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(42)>] value: int) =
            value

type ProvidedGenericInt = Fs1023.ProvidedGenerator<Source = Generic<int>>
type ProvidedGenericString = Fs1023.ProvidedGenerator<Source = Generic<string>>

module UseGenericProvided =
    let intValue = ProvidedGenericInt.Value
    let stringValue = ProvidedGenericString.Value
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

    let private runCommand workingDir fileName args =
        let psi = ProcessStartInfo(fileName)
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        for arg in args do
            psi.ArgumentList.Add arg

        use proc = Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "Command '%s %s' failed with exit code %d.%s%s" fileName (String.concat " " args) proc.ExitCode stdout stderr

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
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
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
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
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
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
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

    [<Fact(Skip = "Generic static arguments currently cause Fs1023 consumer compilation to hang; tracked in Phase 4 follow-up.")>]
    let ``GenericInput_multipleInstantiations`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
        let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

        let consumerPath = Path.Combine(tempDir, "Generic.fs")
        let outputDll = Path.Combine(tempDir, "GenericConsumer.dll")

        let logPath = Path.Combine(tempDir, "generic.log")
        let log message =
            File.AppendAllText(logPath, message + Environment.NewLine)

        try
            log (sprintf "[fs1023][generic] writing provider to %s" providerPath)
            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            log "[fs1023][generic] compiling provider"
            compile providerArgs
            log "[fs1023][generic] provider compiled"

            log "[fs1023][generic] writing generic consumer"
            writeFile consumerPath genericModelSource

            let consumerArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ consumerPath ]))
                    [| "-r:" + providerDll |]

            log "[fs1023][generic] compiling consumer"
            compile consumerArgs
            log (sprintf "[fs1023][generic] consumer compiled -> %s" outputDll)

            let consumerAssembly =
                File.ReadAllBytes(outputDll)
                |> Assembly.Load

            let assertProvidedType typeName =
                let providedType = consumerAssembly.GetType(typeName, throwOnError = true, ignoreCase = false)
                Assert.NotNull(providedType)

                let getStaticStringProperty name =
                    let propertyInfo = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(propertyInfo)
                    propertyInfo.GetValue(null) :?> string

                Assert.Equal("Value", getStaticStringProperty "Value")

            assertProvidedType "Fs1023Consumer.ProvidedGenericInt"
            assertProvidedType "Fs1023Consumer.ProvidedGenericString"
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
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

            let rec collectEntities declarations =
                seq {
                    for decl in declarations do
                        match decl with
                        | FSharpImplementationFileDeclaration.Entity(entity, nested) ->
                            yield entity
                            yield! collectEntities nested
                        | _ -> ()
                }

            let providedEntity =
                projectResults.AssemblyContents.ImplementationFiles
                |> Seq.collect (fun impl -> collectEntities impl.Declarations)
                |> Seq.tryFind (fun entity -> entity.FullName = "Fs1023Consumer.Provided")

            Assert.True(providedEntity.IsSome, "Expected Fs1023Consumer.Provided to be present in the typed tree.")

            let memberNames =
                providedEntity.Value.MembersFunctionsAndValues
                |> Seq.map (fun mfv -> mfv.CompiledName)
                |> Seq.toArray

            Assert.Contains("get_Value", memberNames)
            Assert.Contains("MapParameters", memberNames)

            let assertProperties assemblyPath =
                let consumerAssemblyBytes = File.ReadAllBytes(assemblyPath)
                let consumerAssembly = Assembly.Load(consumerAssemblyBytes)
                let providedType = consumerAssembly.GetType("Fs1023Consumer.Provided", throwOnError = true, ignoreCase = false)

                let staticPropertyNames =
                    providedType.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
                    |> Array.map (fun p -> p.Name)

                let instancePropertyNames =
                    providedType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                    |> Array.map (fun p -> p.Name)

                printfn "[fs1023][reflection] %s static=%A instance=%A" (Path.GetFileName assemblyPath) staticPropertyNames instancePropertyNames

                let getStaticStringProperty name =
                    let propertyInfo = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(propertyInfo)
                    propertyInfo.GetValue(null) :?> string

                Assert.Equal("Value", getStaticStringProperty "Value")
                Assert.Equal("value:required:normal;rest:required:paramarray", getStaticStringProperty "MapParameters")
                Assert.Equal("value:false:false", getStaticStringProperty "OptionalParameter")
                Assert.Equal("value:true:true:42", getStaticStringProperty "OptionalLiteralParameter")
                Assert.Equal("index:Int32", getStaticStringProperty "IndexerParameters")

            compile projectArgs
            assertProperties outputDll

            // Recompile with /standalone to exercise the static-link path and ensure IlxGen-emitted IL survives relocation.
            let standaloneDll = Path.Combine(tempDir, "Consumer.standalone.dll")
            let standaloneArgs = Array.append projectArgs [| "--standalone"; "--out:" + standaloneDll |]
            compile standaloneArgs
            assertProperties standaloneDll
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    let private recordInputSource = """
namespace Fs1023Consumer

type RecordInput =
    { Foo: int
      Bar: string }
    with
        member _.Summary() = sprintf "%d-%s" _.Foo _.Bar
"""

    let private recordConsumerSource = """
namespace Fs1023Consumer

type RecordProvided = Fs1023.ProvidedGenerator<Source = Fs1023Consumer.RecordInput>

module UseRecordProvided =
    let summary = RecordProvided.Value
"""

    let private unionInputSource = """
namespace Fs1023Consumer

type Shape =
    | Circle of radius: int
    | Rectangle of width: int * height: int
    with
        member this.Describe() =
            match this with
            | Circle r -> sprintf "circle:%d" r
            | Rectangle (w, h) -> sprintf "rect:%dx%d" w h
"""

    let private unionConsumerSource = """
namespace Fs1023Consumer

type ShapeProvided = Fs1023.ProvidedGenerator<Source = Fs1023Consumer.Shape>

module UseShapeProvided =
    let summary = ShapeProvided.MapParameters
"""

    let private assertProvidedTypeProperties assemblyPath typeName =
        let assemblyBytes = File.ReadAllBytes(assemblyPath)
        let assembly = Assembly.Load(assemblyBytes)
        let providedType = assembly.GetType(typeName, throwOnError = true, ignoreCase = false)

        let getStaticStringProperty name =
            let propertyInfo = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
            Assert.NotNull(propertyInfo)
            propertyInfo.GetValue(null) :?> string

        getStaticStringProperty

    let private compileAndAssert providerPath providerDll sources outputDll assertions =
        let providerArgs =
            Array.append
                (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                [| "-r:" + providedTypesAssembly |]

        compile providerArgs

        let consumerArgs =
            Array.append
                (mkProjectCommandLineArgs(outputDll, sources))
                [| "-r:" + providerDll |]

        compile consumerArgs
        assertions outputDll

        let standaloneDll = Path.ChangeExtension(outputDll, ".standalone.dll")
        let standaloneArgs = Array.append consumerArgs [| "--standalone"; "--out:" + standaloneDll |]
        compile standaloneArgs
        assertions standaloneDll

    [<Fact>]
    let ``record input compiles generated summaries`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")
            let recordPath = Path.Combine(tempDir, "RecordInput.fs")
            let consumerPath = Path.Combine(tempDir, "RecordConsumer.fs")
            let outputDll = Path.Combine(tempDir, "RecordConsumer.dll")

            writeFile providerPath providerSource
            writeFile recordPath recordInputSource
            writeFile consumerPath recordConsumerSource

            let assertions assemblyPath =
                let getter = assertProvidedTypeProperties assemblyPath "Fs1023Consumer.RecordProvided"
                Assert.Equal("Value", getter "Value")
                Assert.Equal("missing", getter "MapParameters")

            compileAndAssert providerPath providerDll [ recordPath; consumerPath ] outputDll assertions
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``union input compiles generated summaries`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")
            let unionPath = Path.Combine(tempDir, "UnionInput.fs")
            let consumerPath = Path.Combine(tempDir, "UnionConsumer.fs")
            let outputDll = Path.Combine(tempDir, "UnionConsumer.dll")

            writeFile providerPath providerSource
            writeFile unionPath unionInputSource
            writeFile consumerPath unionConsumerSource

            let assertions assemblyPath =
                let getter = assertProvidedTypeProperties assemblyPath "Fs1023Consumer.ShapeProvided"
                Assert.Equal("missing", getter "MapParameters")

            compileAndAssert providerPath providerDll [ unionPath; consumerPath ] outputDll assertions
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``attribute propagation round trips metadata`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")
            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")
            let outputDll = Path.Combine(tempDir, "Consumer.dll")

            writeFile providerPath providerSource
            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSource

            let assertions assemblyPath =
                let getter = assertProvidedTypeProperties assemblyPath "Fs1023Consumer.Provided"
                Assert.Equal("value:false:false", getter "OptionalParameter")
                Assert.Equal("value:true:true:42", getter "OptionalLiteralParameter")
                Assert.Equal("index:Int32", getter "IndexerParameters")

            compileAndAssert providerPath providerDll [ modelPath; consumerPath ] outputDll assertions
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``csharp consumer executes generated member`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")
            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")
            let outputDll = Path.Combine(tempDir, "Consumer.dll")

            writeFile providerPath providerSource
            writeFile modelPath modelSourceInitial
            writeFile consumerPath consumerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let consumerArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            compile consumerArgs

            let programPath = Path.Combine(tempDir, "Program.cs")
            let programSource =
                """
using System;

class Program
{
    static int Main()
    {
        var value = Fs1023Consumer.Provided.Value;
        var summary = Fs1023Consumer.Provided.MapParameters;
        return value == "Value" && summary == "value:required:normal;rest:required:paramarray" ? 0 : 1;
    }
}
"""

            File.WriteAllText(programPath, programSource)

            let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            let fsharpCoreCandidates =
                [ Path.Combine(repoRoot, "artifacts/bin/FSharp.Core/Release/netstandard2.0/FSharp.Core.dll")
                  Path.Combine(repoRoot, "artifacts/bin/FSharp.Core/Release/netstandard2.1/FSharp.Core.dll") ]

            let fsharpCorePath =
                match fsharpCoreCandidates |> List.tryFind File.Exists with
                | Some path -> path
                | None -> failwith "FSharp.Core.dll not found in artifacts/bin/FSharp.Core"

            let csprojPath = Path.Combine(tempDir, "CsConsumer.csproj")
            let csprojContent =
                $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Fs1023Consumer">
      <HintPath>{outputDll}</HintPath>
    </Reference>
    <Reference Include="FSharp.Core">
      <HintPath>{fsharpCorePath}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"""

            File.WriteAllText(csprojPath, csprojContent)

            runCommand tempDir "dotnet" [ "build"; "CsConsumer.csproj"; "-c"; "Release" ]

            let csOutput = Path.Combine(tempDir, "bin", "Release", "net10.0", "CsConsumer.dll")
            Assert.True(File.Exists(csOutput), "Expected dotnet build to produce CsConsumer.dll")

            runCommand tempDir "dotnet" [ csOutput ]
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()
