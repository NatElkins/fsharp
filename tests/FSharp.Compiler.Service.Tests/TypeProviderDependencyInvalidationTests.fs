#nowarn "57"

namespace FSharp.Compiler.Service.Tests

open System
open System.IO
open System.Reflection
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Runtime.Loader
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Service.Tests.Common
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeBasics
open FSharp.Compiler.TypedTreeOps

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
open System.IO
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
    let providerLogPath =
        match Environment.GetEnvironmentVariable("FS1023_PROVIDER_LOG") with
        | null
        | "" -> "/tmp/fs1023_provider.log"
        | custom -> custom

    let appendProviderLog message =
        let entry = sprintf "%s %s%s" (DateTime.UtcNow.ToString("O")) message Environment.NewLine
        try
            File.AppendAllText(providerLogPath, entry)
        with _ -> ()

    do
        let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

        generator.DefineStaticParameters(parameters, fun typeName args ->
            let sourceType = args.[0] :?> Type
            let logProvider stage =
                let sourceName =
                    if isNull sourceType then
                        "<null>"
                    else
                        let fullName = sourceType.FullName
                        if isNull fullName then sourceType.Name else fullName
                let message = sprintf "[fs1023][provider] %s type=%s source=%s" stage typeName sourceName
                appendProviderLog message
                printfn "%s" message
            logProvider "define-start"
            let provided = ProvidedTypeDefinition(assembly, namespaceName, typeName, Some typeof<obj>, isErased = false, hideObjectMethods = true)

            let logMethods label =
                logProvider (sprintf "methods-begin:%s" label)
                let methods =
                    sourceType.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun m ->
                        let parameters =
                            m.GetParameters()
                            |> Array.map (fun p -> sprintf "%s:%s" p.Name p.ParameterType.Name)
                            |> String.concat ","
                        sprintf "%s(%s)" m.Name parameters)
                printfn "[fs1023][%s][methods] %s" label (String.concat "; " methods)
                logProvider (sprintf "methods-end:%s" label)

            let logProperties label =
                logProvider (sprintf "properties-begin:%s" label)
                let properties =
                    sourceType.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun p ->
                        let indexers =
                            p.GetIndexParameters()
                            |> Array.map (fun idx -> sprintf "%s:%s" idx.Name idx.ParameterType.Name)
                            |> String.concat ","
                        sprintf "%s[%s]:%s" p.Name indexers p.PropertyType.Name)
                printfn "[fs1023][%s][properties] %s" label (String.concat "; " properties)
                logProvider (sprintf "properties-end:%s" label)

            logMethods typeName
            logProperties typeName

            let logParameterDetails (m: MethodInfo) =
                logProvider (sprintf "method-params-begin:%s" m.Name)
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
                logProvider (sprintf "method-params-end:%s" m.Name)

            let logPropertyDetails (p: PropertyInfo) =
                logProvider (sprintf "property-params-begin:%s" p.Name)
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
                logProvider (sprintf "property-params-end:%s" p.Name)

            sourceType.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.iter logParameterDetails

            sourceType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.iter logPropertyDetails

            let addMemberFromProperty (pi: PropertyInfo) =
                if pi.GetIndexParameters().Length = 0 then
                    logProvider (sprintf "addMember-begin:%s" pi.Name)
                    printfn "[fs1023][addMember] adding property %s" pi.Name
                    let getter (_args: Expr list) =
                        let name = pi.Name
                        <@@ name @@>
                    let property = ProvidedProperty(pi.Name, typeof<string>, isStatic = true, getterCode = getter)
                    provided.AddMember property
                    logProvider (sprintf "addMember-end:%s" pi.Name)

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
                    | Some p ->
                        let attributeNames =
                            p.GetCustomAttributes(false)
                            |> Seq.map (fun attr -> attr.GetType().Name)
                            |> Seq.toArray
                        let attributeSummary =
                            if attributeNames.Length = 0 then
                                "none"
                            else
                                String.concat "," attributeNames
                        sprintf "%s:%b:%b:%s" p.Name p.IsOptional p.HasDefaultValue attributeSummary

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

            let eventSummary =
                let events =
                    sourceType.GetEvents(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun ev -> ev.Name)
                if events.Length = 0 then
                    "none"
                else
                    String.concat ";" events

            let moduleTypeSummary =
                sourceType.Module.GetTypes()
                |> Array.filter (fun ty ->
                    let ns = ty.Namespace
                    let targetNs = sourceType.Namespace
                    if String.IsNullOrEmpty targetNs then
                        true
                    else
                        String.Equals(ns, targetNs, StringComparison.Ordinal))
                |> Array.map (fun ty -> ty.Name)
                |> Array.distinct
                |> Array.sort
                |> function
                    | [||] -> "none"
                    | names -> String.concat ";" names

            let hiddenMethodVisibility =
                let publicLookup =
                    sourceType.GetMethod("HiddenSummary", BindingFlags.Public ||| BindingFlags.Instance)
                let nonPublicLookup =
                    sourceType.GetMethod("HiddenSummary", BindingFlags.NonPublic ||| BindingFlags.Instance)
                match isNull publicLookup, isNull nonPublicLookup with
                | true, false -> "nonpublic-only"
                | false, _ -> "public-visible"
                | true, true -> "missing"

            printfn "[fs1023][%s][summary] Map=%s Optional=%s OptionalLiteral=%s Indexer=%s" typeName mapSummary optionalSummary optionalLiteralSummary indexerSummary

            let addSummaryProperty name value =
                logProvider (sprintf "addSummary-begin:%s" name)
                let getter (_: Expr list) = Expr.Value value
                let property = ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = getter)
                provided.AddMember property
                logProvider (sprintf "addSummary-end:%s" name)

            addSummaryProperty "MapParameters" mapSummary
            addSummaryProperty "OptionalParameter" optionalSummary
            addSummaryProperty "OptionalLiteralParameter" optionalLiteralSummary
            addSummaryProperty "IndexerParameters" indexerSummary
            let propertyVisibility =
                let publicLookup =
                    sourceType.GetProperty("HiddenResult", BindingFlags.Public ||| BindingFlags.Instance)
                let nonPublicLookup =
                    sourceType.GetProperty("HiddenResult", BindingFlags.NonPublic ||| BindingFlags.Instance)
                match isNull publicLookup, isNull nonPublicLookup with
                | true, false -> "nonpublic-only"
                | false, _ -> "public-visible"
                | true, true -> "missing"

            addSummaryProperty "EventSummary" eventSummary
            addSummaryProperty "ModuleTypeSummary" moduleTypeSummary
            addSummaryProperty "HiddenMethodVisibility" hiddenMethodVisibility
            addSummaryProperty "HiddenPropertyVisibility" propertyVisibility

            logProvider "define-end"
            provided)

    do this.AddNamespace(namespaceName, [ generator ])

[<assembly: TypeProviderAssembly>]
do ()
"""

    let providerNonGeneratedSource = """
namespace Fs1023Invalid

open System
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes

[<TypeProvider>]
type NonGeneratedProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = Assembly.GetExecutingAssembly()
    let namespaceName = "Fs1023Invalid"
    let generator = ProvidedTypeDefinition(assembly, namespaceName, "NonGeneratedGenerator", Some typeof<obj>, isErased = true)

    do
        let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

        generator.DefineStaticParameters(parameters, fun typeName _ ->
            let provided = ProvidedTypeDefinition(assembly, namespaceName, typeName, Some typeof<obj>, isErased = true)
            let getter (_args: Expr list) = <@@ "invalid" @@>
            let property = ProvidedProperty("Value", typeof<string>, isStatic = true, getterCode = getter)
            provided.AddMember property
            provided)

        this.AddNamespace(namespaceName, [ generator ])

[<assembly: TypeProviderAssembly>]
do ()
"""

    let private modelSourceInitial = """
namespace Fs1023Consumer

open System
open Microsoft.FSharp.Control

type Model =
    { Value: int }
    with
        [<CLIEvent>]
        member _.ValueChanged = Event<EventHandler, EventArgs>().Publish
        member _.Map(value: int, [<System.ParamArrayAttribute>] rest: string[]) = value + rest.Length
        member _.Optional(?value: int) = defaultArg value 0
        member _.Item
            with get(index: int) = index
        member _.OptionalLiteral([<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(42)>] value: int) =
            value
        member private this.HiddenSummary() = this.Value
        member private this.HiddenResult
            with get () = this.Value
            and set _ = ()
"""

    let private modelSignatureSource = """
namespace Fs1023Consumer

type Model =
    { Value: int }
    with
        member Map : value:int * rest:string[] -> int
        member Optional : ?value:int -> int
        member Item : index:int -> int
        member OptionalLiteral : value:int -> int
"""

    let private modelSourceRenamed = """
namespace Fs1023Consumer

open System
open Microsoft.FSharp.Control

type Model =
    { Renamed: int }
    with
        [<CLIEvent>]
        member _.ValueChanged = Event<EventHandler, EventArgs>().Publish
        member _.Map(value: int, [<System.ParamArrayAttribute>] rest: string[]) = value + rest.Length
        member _.Optional(?value: int) = defaultArg value 0
        member _.Item
            with get(index: int) = index
        member _.OptionalLiteral([<System.Runtime.InteropServices.Optional; System.Runtime.InteropServices.DefaultParameterValue(42)>] value: int) =
            value
        member private this.HiddenSummary() = this.Renamed
        member private this.HiddenResult
            with get () = this.Renamed
            and set _ = ()
"""

    let private signatureModelSignatureSource = """
namespace Fs1023Signature

type SignatureModel =
    { Value: int }
"""

    let private signatureModelImplementationSource = """
namespace Fs1023Signature

type SignatureModel =
    { Value: int }
    with
        member this.ValueString() = this.Value.ToString()
"""

    let private signatureConsumerSource = """
namespace Fs1023Signature

type Provided = Fs1023.ProvidedGenerator<Source = Fs1023Signature.SignatureModel>

module UseProvided =
    let valueName = Provided.Value
"""

    let private appendSignatureComment baseText comment =
        baseText + sprintf "\n// %s\n" comment

    let private modelSignatureSourceWithComment comment =
        appendSignatureComment modelSignatureSource comment

    let private signatureModelSignatureSourceWithComment comment =
        appendSignatureComment signatureModelSignatureSource comment

    let private consumerSource = """
namespace Fs1023Consumer

type Provided = Fs1023.ProvidedGenerator<Source = Fs1023Consumer.Model>

module UseProvided =
    let valueName = Provided.Value
"""

    let private mutableProviderSource = """
namespace MutableFs1023

open System
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes

type MutableSummaryRuntime() =
    static let mutable summary = "unconfigured"
    static member Summary
        with get () = summary
        and set (value: string) =
            summary <- value

[<TypeProvider>]
type MutableProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = Assembly.GetExecutingAssembly()
    let namespaceName = "MutableFs1023"
    let generator =
        ProvidedTypeDefinition(
            assembly,
            namespaceName,
            "MutableGenerator",
            baseType = Some typeof<obj>,
            isErased = false,
            hideObjectMethods = true)

    let summaryProperty =
        typeof<MutableSummaryRuntime>.GetProperty("Summary", BindingFlags.Public ||| BindingFlags.Static)

    do
        let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

        generator.DefineStaticParameters(parameters, fun typeName _ ->
            let provided =
                ProvidedTypeDefinition(
                    assembly,
                    namespaceName,
                    typeName,
                    baseType = Some typeof<obj>,
                    isErased = false,
                    hideObjectMethods = true)

            let getter (_: Expr list) =
                Expr.Call(summaryProperty.GetMethod, [])

            let setter (args: Expr list) =
                match args with
                | [ valueExpr ] ->
                    let coerced = Expr.Coerce(valueExpr, typeof<string>)
                    Expr.Call(summaryProperty.SetMethod, [ coerced ])
                | _ ->
                    failwithf "MutableSummary setter expected a single argument, got %d" args.Length

            let property =
                ProvidedProperty(
                    "MutableSummary",
                    propertyType = typeof<string>,
                    isStatic = true,
                    getterCode = getter,
                    setterCode = setter)

            provided.AddMember property

            let ctor =
                ProvidedConstructor([], invokeCode = fun _ -> <@@ () @@>)

            provided.AddMember ctor
            provided)

        this.AddNamespace(namespaceName, [ generator ])

[<assembly: TypeProviderAssembly>]
do ()
"""

    let private mutableModelSource = """
namespace MutableFs1023Model

type MutableModel =
    { MutableSummary: string }
    member this.InitialSummary = this.MutableSummary
"""

    let private mutableConsumerSource = """
namespace MutableFs1023Consumer

type Provided = MutableFs1023.MutableGenerator<Source = MutableFs1023Model.MutableModel>

module UseProvided =
    let configure summary =
        Provided.MutableSummary <- summary
        Provided.MutableSummary
"""

    let private eventProviderSource = """
namespace Fs1023Event

open System
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes

type EventRuntime() =
    static let triggered = Event<EventHandler, EventArgs>()
    static member AddHandler(handler: EventHandler) = triggered.Publish.AddHandler handler
    static member RemoveHandler(handler: EventHandler) = triggered.Publish.RemoveHandler handler
    static member Trigger(sender: obj) = triggered.Trigger(sender, EventArgs.Empty)

[<TypeProvider>]
type EventProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = Assembly.GetExecutingAssembly()
    let namespaceName = "Fs1023Event"
    let generator =
        ProvidedTypeDefinition(
            assembly,
            namespaceName,
            "EventGenerator",
            baseType = Some typeof<obj>,
            isErased = false,
            hideObjectMethods = true)

    do
        let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

        let addHandlerMethod =
            typeof<EventRuntime>.GetMethod("AddHandler", BindingFlags.Public ||| BindingFlags.Static)

        let removeHandlerMethod =
            typeof<EventRuntime>.GetMethod("RemoveHandler", BindingFlags.Public ||| BindingFlags.Static)

        let triggerMethod =
            typeof<EventRuntime>.GetMethod("Trigger", BindingFlags.Public ||| BindingFlags.Static)

        generator.DefineStaticParameters(parameters, fun typeName _ ->
            let provided =
                ProvidedTypeDefinition(
                    assembly,
                    namespaceName,
                    typeName,
                    baseType = Some typeof<obj>,
                    isErased = false,
                    hideObjectMethods = true)

            let adder (args: Expr list) =
                match args with
                | [ handler ] ->
                    let coerced = Expr.Coerce(handler, typeof<EventHandler>)
                    Expr.Call(addHandlerMethod, [ coerced ])
                | other -> failwithf "Unexpected add args %d" other.Length

            let remover (args: Expr list) =
                match args with
                | [ handler ] ->
                    let coerced = Expr.Coerce(handler, typeof<EventHandler>)
                    Expr.Call(removeHandlerMethod, [ coerced ])
                | other -> failwithf "Unexpected remove args %d" other.Length

            let eventDef =
                ProvidedEvent(
                    eventName = "Triggered",
                    eventHandlerType = typeof<EventHandler>,
                    adderCode = adder,
                    removerCode = remover,
                    isStatic = true)

            provided.AddMember eventDef

            let fireMethod =
                ProvidedMethod(
                    "Fire",
                    parameters = [ ProvidedParameter("sender", typeof<obj>) ],
                    returnType = typeof<Void>,
                    isStatic = true,
                    invokeCode = fun args ->
                        match args with
                        | [ sender ] -> Expr.Call(triggerMethod, [ sender ])
                        | _ -> <@@ () @@>)

            provided.AddMember fireMethod
            provided)

        this.AddNamespace(namespaceName, [ generator ])

[<assembly: TypeProviderAssembly>]
do ()
"""

    let private eventModelSource = """
namespace Fs1023EventModel

type EventModel = { Name: string }
"""

    let private eventConsumerSource = """
namespace Fs1023EventConsumer

type Provided = Fs1023Event.EventGenerator<Source = Fs1023EventModel.EventModel>

module Sink =
    let fire sender =
        Provided.Triggered.AddHandler(System.EventHandler(fun _ _ -> ()))
        Provided.Fire(sender)
"""

    let private jsonSerializerProviderSource = """
namespace Fs1023Json

open System
open System.Text.Json

namespace Fs1023Json.ProviderImplementation

open System
open System.Reflection
open System.Text.Json
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Fs1023Json

[<TypeProvider>]
type JsonSerializerProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = System.Reflection.Assembly.GetExecutingAssembly()
    let namespaceName = "Fs1023Json"
    let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]
    let log message =
        printfn "[fs1023][json-provider] %s" message

    let buildType typeName (args: obj[]) =
        try
            let sourceType = args.[0] :?> Type
            log (sprintf "buildType typeName=%s source=%s" typeName sourceType.FullName)
            let provided =
                ProvidedTypeDefinition(assembly, namespaceName, typeName, Some typeof<obj>, isErased = false, hideObjectMethods = true)

            let typeGetMethod =
                typeof<Type>.GetMethod("GetType", BindingFlags.Public ||| BindingFlags.Static, null, [| typeof<string>; typeof<bool> |], null)
            let sourceTypeName = sourceType.AssemblyQualifiedName
            let resolveSourceTypeExpr () =
                Expr.Call(typeGetMethod, [ Expr.Value(sourceTypeName); Expr.Value(true) ])

            let serializeMethod =
                typeof<JsonSerializer>.GetMethod(
                    "Serialize",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<obj>; typeof<Type>; typeof<JsonSerializerOptions> |],
                    null)

            let deserializeMethod =
                typeof<JsonSerializer>.GetMethod(
                    "Deserialize",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<string>; typeof<Type>; typeof<JsonSerializerOptions> |],
                    null)

            let toJson =
                ProvidedMethod(
                    "ToJson",
                    parameters = [ ProvidedParameter("value", sourceType) ],
                    returnType = typeof<string>,
                    isStatic = true,
                    invokeCode = fun args ->
                        let valueExpr = Expr.Coerce(args.[0], typeof<obj>)
                        Expr.Call(serializeMethod, [ valueExpr; resolveSourceTypeExpr(); Expr.Value(null, typeof<JsonSerializerOptions>) ]))

            try
                provided.AddMember toJson
            with ex ->
                log (sprintf "addMember ToJson failed ex=%s" (ex.ToString()))
                reraise()

            let fromJson =
                ProvidedMethod(
                    "FromJson",
                    parameters = [ ProvidedParameter("json", typeof<string>) ],
                    returnType = sourceType,
                    isStatic = true,
                    invokeCode = fun args ->
                        let jsonArg = Expr.Coerce(args.[0], typeof<string>)
                        let deserialized =
                            Expr.Call(deserializeMethod, [ jsonArg; resolveSourceTypeExpr(); Expr.Value(null, typeof<JsonSerializerOptions>) ])
                        Expr.Coerce(deserialized, sourceType))

            try
                provided.AddMember fromJson
            with ex ->
                log (sprintf "addMember FromJson failed ex=%s" (ex.ToString()))
                reraise()
            provided
        with ex ->
            log (sprintf "buildType fail typeName=%s ex=%s" typeName (ex.ToString()))
            reraise()

    let root = ProvidedTypeDefinition(assembly, namespaceName, "JsonSerializerProvider", Some typeof<obj>, isErased = false)

    do
        log "registering root"
        root.DefineStaticParameters(parameters, buildType)
        this.AddNamespace(namespaceName, [ root ])

[<assembly:TypeProviderAssembly>]
do ()
"""

    let private jsonSerializerModelSource = """
namespace SampleJson

type Order =
    { Id: int
      Customer: string
      Items: string list }
"""

    let private jsonSerializerConsumerSource = """
namespace SampleJson

type OrderJson = Fs1023Json.JsonSerializerProvider<Source = SampleJson.Order>

module Tests =
    let roundTrip () =
        let original =
            { Order.Id = 42
              Customer = "Alice"
              Items = [ "Apples"; "Bananas" ] }

        let json = OrderJson.ToJson original
        let clone = OrderJson.FromJson json
        clone = original
"""

    let private genericModelSource = """
namespace Fs1023Consumer

type Generic<'T> =
    { Value: 'T }
    with
        member _.Map(value: 'T, [<System.ParamArrayAttribute>] rest: string[]) =
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

    let private consumerSourceNonGeneratedProvidedType = """
namespace Fs1023Consumer

type Model = { Value: int }

type Provided = Fs1023Invalid.NonGeneratedGenerator<Source = Model>

module UseProvided =
    let name = Provided.Value
"""

    let private ensureSuccess (diagnostics: FSharpDiagnostic[]) =
        diagnostics
        |> Array.iter (fun d ->
            if d.Severity = FSharpDiagnosticSeverity.Error then
                printfn "[fs1023][diagnostic] %s" d.Message
            if d.Severity = FSharpDiagnosticSeverity.Error then
                failwithf "Compilation error (%s:%d,%d): %s" d.FileName d.StartLine d.StartColumn d.Message)

    let private runCompileWithChecker (checkerInstance: FSharpChecker) args =
        let diagnostics, exnOpt = checkerInstance.Compile args |> Async.RunImmediate
        ensureSuccess diagnostics

        match exnOpt with
        | Some ex -> raise ex
        | None -> ()

    let private compile args = runCompileWithChecker checker args

    let private writeFile (path: string) (contents: string) =
        let directory = Path.GetDirectoryName(path)

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory(directory) |> ignore

        File.WriteAllText(path, contents)

    let private configureProviderLog tempDir =
        let logPath = Path.Combine(tempDir, "provider.log")
        let previous = Environment.GetEnvironmentVariable("FS1023_PROVIDER_LOG")
        Environment.SetEnvironmentVariable("FS1023_PROVIDER_LOG", logPath)
        if File.Exists(logPath) then
            File.Delete(logPath)

        logPath,
        { new IDisposable with
            member _.Dispose() = Environment.SetEnvironmentVariable("FS1023_PROVIDER_LOG", previous) }

    let private countProviderRuns logPath =
        if File.Exists(logPath) then
            File.ReadLines(logPath)
            |> Seq.filter (fun line -> line.Contains("[fs1023][provider] define-start"))
            |> Seq.length
        else
            0

    let private tyconRefProperty =
        lazy (
            typeof<FSharpEntity>.GetProperty(
                "Entity",
                BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            )

    let private getTyconRef (entity: FSharpEntity) =
        match tyconRefProperty.Value with
        | null -> failwith "Unable to access FSharpEntity.Entity via reflection."
        | prop -> prop.GetValue(entity, null) :?> TyconRef

    let private projectContextCcuField =
        lazy (
            typeof<FSharpProjectContext>.GetField(
                "thisCcu",
                BindingFlags.Instance ||| BindingFlags.NonPublic)
            )

    let private getProjectContextCcu (context: FSharpProjectContext) =
        match projectContextCcuField.Value with
        | null -> failwith "Unable to access FSharpProjectContext.thisCcu via reflection."
        | field -> field.GetValue(context) :?> CcuThunk

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

    let private compileWithLogging (log: (string -> unit) option) (label: string) (args: string[]) (projectInfoOpt: (string * string list) option) =
        let logMsg message =
            match log with
            | Some f -> f message
            | None -> ()

        let timestamp () = DateTime.UtcNow.ToString("O")
        let safeLabel = label.Replace(" ", "-")
        let submissionId = Guid.NewGuid().ToString("N")
        let buildTargets =
            let parsedTargets =
                args
                |> Array.choose (fun arg ->
                    if arg.StartsWith("--target:", StringComparison.OrdinalIgnoreCase) then
                        Some(arg.Substring("--target:".Length))
                    elif arg.Equals("--standalone", StringComparison.OrdinalIgnoreCase) then
                        Some "standalone"
                    elif arg.Equals("-a", StringComparison.OrdinalIgnoreCase) || arg.Equals("--target:library", StringComparison.OrdinalIgnoreCase) then
                        Some "library"
                    elif arg.Equals("--target:module", StringComparison.OrdinalIgnoreCase) then
                        Some "module"
                    else
                        None)

            if Array.isEmpty parsedTargets then
                [| "Build" |]
            else
                parsedTargets

        let logBuildRequest phase extra =
            match projectInfoOpt with
            | Some (projectPath, files) ->
                let filesSummary =
                    match files with
                    | [] -> "(no source overrides)"
                    | xs -> xs |> List.map Path.GetFileName |> String.concat ","

                logMsg (
                    sprintf
                        "[fs1023][compile] %s build-request %s project=%s evaluation=%s targets=%s files=%s %s"
                        (timestamp ())
                        phase
                        projectPath
                        submissionId
                        (String.concat ";" buildTargets)
                        filesSummary
                        extra)
            | None -> ()

        let freshCheckerRequested =
            match Environment.GetEnvironmentVariable("FS1023_FORCE_FRESH_CHECKER") with
            | null
            | "" -> false
            | value when value.Equals("0", StringComparison.OrdinalIgnoreCase) -> false
            | _ -> true

        let skipParseCheck =
            match Environment.GetEnvironmentVariable("FS1023_SKIP_PARSE_AND_CHECK") with
            | null
            | "" -> false
            | value when value.Equals("0", StringComparison.OrdinalIgnoreCase) -> false
            | _ -> true

        let freshCheckerOpt =
            if freshCheckerRequested then
                Some(
                    FSharpChecker.Create(
                        keepAssemblyContents = false,
                        useTransparentCompiler = FSharp.Test.CompilerAssertHelpers.UseTransparentCompiler))
            else
                None

        let activeChecker =
            match freshCheckerOpt with
            | Some checkerInstance -> checkerInstance
            | None -> checker

        let msbuildLogDir =
            let dir = Path.Combine(Path.GetTempPath(), "fs1023-msbuild", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(dir) |> ignore
            dir
        let msbuildLogFile = Path.Combine(msbuildLogDir, safeLabel + ".binlog")

        let setEnv name value =
            let previous = Environment.GetEnvironmentVariable(name)
            Environment.SetEnvironmentVariable(name, value)
            { new IDisposable with
                member _.Dispose() = Environment.SetEnvironmentVariable(name, previous) }

        use _debugPath = setEnv "MSBUILDDEBUGPATH" msbuildLogDir
        use _logFile = setEnv "MSBUILDLOGFILE" msbuildLogFile

        logMsg (sprintf "[fs1023][compile] %s begin %s" (timestamp ()) label)
        logMsg (sprintf "[fs1023][compile] args(%s): %s" label (String.concat " " args))
        logMsg (sprintf "[fs1023][compile] msbuild logs for %s -> dir=%s file=%s" label msbuildLogDir msbuildLogFile)
        if freshCheckerRequested then
            logMsg (sprintf "[fs1023][compile] %s forcing fresh checker instance" label)
        logBuildRequest "begin" ""

        let runParseAndCheck () =
            match projectInfoOpt, skipParseCheck with
            | Some (projectPath, files), false when not (List.isEmpty files) ->
                logBuildRequest "parse+check-begin" (sprintf "projectPath=%s" projectPath)
                let parseWatch = Stopwatch.StartNew()
                let projectOptions =
                    { activeChecker.GetProjectOptionsFromCommandLineArgs(projectPath, args) with
                        SourceFiles = files |> List.toArray }

                let projectResults = activeChecker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate
                ensureSuccess projectResults.Diagnostics
                parseWatch.Stop()
                logBuildRequest
                    "parse+check-end"
                    (sprintf "durationMs=%d diagnostics=%d" parseWatch.ElapsedMilliseconds projectResults.Diagnostics.Length)
            | Some _, true ->
                logBuildRequest "parse+check-skip" "env=FS1023_SKIP_PARSE_AND_CHECK"
            | _ -> ()

        try
            runParseAndCheck ()
            let checkerKind = if freshCheckerRequested then "fresh" else "shared"
            logBuildRequest "compile-begin" (sprintf "invoking checker.Compile (%s)" checkerKind)
            let compileWatch = Stopwatch.StartNew()
            let compileCallWatch = Stopwatch.StartNew()
            logBuildRequest "compile-call-begin" (sprintf "checker=%s" checkerKind)
            try
                runCompileWithChecker activeChecker args
            finally
                compileCallWatch.Stop()
                logBuildRequest "compile-call-end" (sprintf "elapsedMs=%d" compileCallWatch.ElapsedMilliseconds)
            compileWatch.Stop()
            logBuildRequest "compile-end" (sprintf "durationMs=%d" compileWatch.ElapsedMilliseconds)
            logBuildRequest "end" ""
            logMsg (sprintf "[fs1023][compile] %s end %s" (timestamp ()) label)
        with ex ->
            logBuildRequest "fail" (sprintf "message=%s" ex.Message)
            logMsg (sprintf "[fs1023][compile] %s fail %s: %s" (timestamp ()) label ex.Message)
            reraise()

    [<Fact>]
    let ``type provider re-runs when source type changes`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let providerLogPath, restoreProviderLogEnv = configureProviderLog tempDir
        use _restoreProviderLog = restoreProviderLogEnv

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

            let initialProviderRuns = countProviderRuns providerLogPath
            Assert.True(initialProviderRuns > 0, "Expected provider to run at least once during the initial compile.")

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

            let rerunCount = countProviderRuns providerLogPath
            Assert.True(
                rerunCount > initialProviderRuns,
                sprintf "Expected provider to re-run after source change (initial=%d, updated=%d)." initialProviderRuns rerunCount)
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``type provider re-runs when signature file changes`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let providerLogPath, restoreProviderLogEnv = configureProviderLog tempDir
        use _restoreProviderLog = restoreProviderLogEnv

        try
            let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")

            writeFile providerPath providerSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelSignaturePath = Path.Combine(tempDir, "SignatureModel.fsi")
            let modelPath = Path.Combine(tempDir, "SignatureModel.fs")
            let consumerPath = Path.Combine(tempDir, "SignatureConsumer.fs")

            writeFile modelSignaturePath signatureModelSignatureSource
            writeFile modelPath signatureModelImplementationSource
            writeFile consumerPath signatureConsumerSource

            let outputDll = Path.Combine(tempDir, "Consumer.dll")
            let projectFile = Path.Combine(tempDir, "Consumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelSignaturePath; modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            let projectOptions =
                { checker.GetProjectOptionsFromCommandLineArgs(projectFile, projectArgs) with
                    SourceFiles = [| modelSignaturePath; modelPath; consumerPath |] }

            let checkProject () = checker.ParseAndCheckProject(projectOptions) |> Async.RunImmediate

            let initialResults = checkProject()
            ensureSuccess initialResults.Diagnostics

            let initialProviderRuns = countProviderRuns providerLogPath
            Assert.True(initialProviderRuns > 0, "Expected provider to run at least once during the initial compile.")
            Assert.Contains(modelSignaturePath |> getFullPath, initialResults.DependencyFiles |> Array.map getFullPath)

            // Modify only the signature file (add a comment) to trigger invalidation.
            let updatedSignature = signatureModelSignatureSourceWithComment("signature tweak")
            writeFile modelSignaturePath updatedSignature
            checker.NotifyFileChanged(modelSignaturePath, projectOptions) |> Async.RunImmediate

            let updatedResults = checkProject()
            ensureSuccess updatedResults.Diagnostics

            let rerunCount = countProviderRuns providerLogPath
            Assert.True(
                rerunCount > initialProviderRuns,
                sprintf "Expected provider to re-run after signature change (initial=%d, updated=%d)." initialProviderRuns rerunCount)
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``mutable provided type exposes setter and constructor`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "MutableProvider.fs")
            let providerDll = Path.Combine(tempDir, "MutableProvider.dll")

            writeFile providerPath mutableProviderSource

            let providerArgs =
                [| yield! mkProjectCommandLineArgs(providerDll, [ providerPath ])
                   yield "-r:" + providedTypesAssembly |]
                |> Array.filter (fun arg -> not (arg.StartsWith("--langversion") || arg.StartsWith("-langversion")))
                |> Array.append [| "--mlcompatibility"; "--langversion:5.0" |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "MutableModel.fs")
            let consumerPath = Path.Combine(tempDir, "MutableConsumer.fs")

            writeFile modelPath mutableModelSource
            writeFile consumerPath mutableConsumerSource

            let outputDll = Path.Combine(tempDir, "MutableConsumer.dll")
            let projectFile = Path.Combine(tempDir, "MutableConsumer.fsproj")

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
                |> Seq.tryFind (fun entity -> entity.FullName = "MutableFs1023Consumer.Provided")

            Assert.True(providedEntity.IsSome, "Expected MutableFs1023Consumer.Provided to appear in the typed tree.")

            let memberNames =
                providedEntity.Value.MembersFunctionsAndValues
                |> Seq.map (fun mfv -> mfv.CompiledName)
                |> Seq.toArray

            Assert.Contains("set_MutableSummary", memberNames)
            Assert.Contains(".ctor", memberNames)

            let assertMutable assemblyPath =
                let consumerAssemblyBytes = File.ReadAllBytes(assemblyPath)
                let consumerAssembly = Assembly.Load(consumerAssemblyBytes)
                let providedType = consumerAssembly.GetType("MutableFs1023Consumer.Provided", throwOnError = true, ignoreCase = false)

                let summaryProperty =
                    providedType.GetProperty("MutableSummary", BindingFlags.Public ||| BindingFlags.Static)

                Assert.NotNull(summaryProperty)
                summaryProperty.SetValue(null, "configured")
                Assert.Equal("configured", summaryProperty.GetValue(null) :?> string)

                Assert.NotNull(Activator.CreateInstance(providedType))

            compile projectArgs
            assertMutable outputDll

            let standaloneDll = Path.Combine(tempDir, "MutableConsumer.standalone.dll")
            let standaloneArgs = Array.append projectArgs [| "--standalone"; "--out:" + standaloneDll |]
            compile standaloneArgs
            assertMutable standaloneDll
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``provided event surfaces in emitted IL`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "EventProvider.fs")
            let providerDll = Path.Combine(tempDir, "EventProvider.dll")

            writeFile providerPath eventProviderSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "EventModel.fs")
            let consumerPath = Path.Combine(tempDir, "EventConsumer.fs")

            writeFile modelPath eventModelSource
            writeFile consumerPath eventConsumerSource

            let outputDll = Path.Combine(tempDir, "EventConsumer.dll")
            let projectFile = Path.Combine(tempDir, "EventConsumer.fsproj")

            let projectArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ]))
                    [| "-r:" + providerDll |]

            let assertEvent assemblyPath =
                let consumerAssemblyBytes = File.ReadAllBytes(assemblyPath)
                let consumerAssembly = Assembly.Load(consumerAssemblyBytes)
                let providedType = consumerAssembly.GetType("Fs1023EventConsumer.Provided", throwOnError = true, ignoreCase = false)

                let eventInfo =
                    providedType.GetEvent("Triggered", BindingFlags.Public ||| BindingFlags.Static)

                Assert.NotNull(eventInfo)
                Assert.NotNull(eventInfo.GetAddMethod())
                Assert.NotNull(eventInfo.GetRemoveMethod())

                let invoked = ref false
                let handler = EventHandler(fun _ _ -> invoked := true)
                eventInfo.AddEventHandler(null, handler)

                let fireMethod =
                    providedType.GetMethod("Fire", BindingFlags.Public ||| BindingFlags.Static)

                Assert.NotNull(fireMethod)
                fireMethod.Invoke(null, [| box null |]) |> ignore
                Assert.True(!invoked, "Expected handler state to be updated")

            compile projectArgs
            assertEvent outputDll

            let standaloneDll = Path.Combine(tempDir, "EventConsumer.standalone.dll")
            let standaloneArgs = Array.append projectArgs [| "--standalone"; "--out:" + standaloneDll |]
            compile standaloneArgs
            assertEvent standaloneDll
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
    let ``non-generated provider types are rejected`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "Fs1023InvalidProvider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023InvalidProvider.dll")

            writeFile providerPath providerNonGeneratedSource

            let providerArgs =
                Array.append
                    (mkProjectCommandLineArgs(providerDll, [ providerPath ]))
                    [| "-r:" + providedTypesAssembly |]

            compile providerArgs

            let consumerPath = Path.Combine(tempDir, "Consumer.fs")
            writeFile consumerPath consumerSourceNonGeneratedProvidedType

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

            errors |> Array.iter (fun d -> printfn "[non-generated] %s" d.Message)

            Assert.True(errors.Length > 0, "Expected non-generated provider type to be rejected.")
            let messageMatches (e: FSharpDiagnostic) =
                e.Message.Contains("non-generated type")
                || e.Message.Contains("type could not be found in that assembly")
            Assert.True(errors |> Array.exists messageMatches, "Expected diagnostic mentioning non-generated type restriction.")
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
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
                let eventSummary = readStaticProperty "EventSummary"
                let moduleSummary = readStaticProperty "ModuleTypeSummary"
                let hiddenVisibility = readStaticProperty "HiddenMethodVisibility"
                let hiddenPropertyVisibility = readStaticProperty "HiddenPropertyVisibility"

                printfn "[fs1023][assert] MapParameters=%s" mapSummary
                printfn "[fs1023][assert] OptionalParameter=%s" optionalSummary
                printfn "[fs1023][assert] OptionalLiteralParameter=%s" optionalLiteralSummary
                printfn "[fs1023][assert] IndexerParameters=%s" indexerSummary

                Assert.Equal("value:required:normal;rest:required:paramarray", mapSummary)
                Assert.Equal("value:true:true:OptionalArgumentAttribute", optionalSummary)
                Assert.Equal("value:true:true:42", optionalLiteralSummary)
                Assert.Equal("index:Int32", indexerSummary)
                Assert.Equal("ValueChanged", eventSummary)
                Assert.Equal("Model;Provided;UseProvided", moduleSummary)
                Assert.Equal("nonpublic-only", hiddenVisibility)
                Assert.Equal("nonpublic-only", hiddenPropertyVisibility)
            finally
                context.Unload()
        finally
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``GenericInput_multipleInstantiations`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let providerPath = Path.Combine(tempDir, "Fs1023Provider.fs")
        let providerDll = Path.Combine(tempDir, "Fs1023Provider.dll")
        let providerProject = Path.Combine(tempDir, "generic-provider.fsproj")

        let consumerPath = Path.Combine(tempDir, "Generic.fs")
        let outputDll = Path.Combine(tempDir, "GenericConsumer.dll")
        let consumerProject = Path.Combine(tempDir, "generic-consumer.fsproj")

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

            compileWithLogging (Some log) "generic-provider" providerArgs (Some (providerProject, [ providerPath ]))

            log "[fs1023][generic] writing generic consumer"
            writeFile consumerPath genericModelSource

            let consumerArgs =
                Array.append
                    (mkProjectCommandLineArgs(outputDll, [ consumerPath ]))
                    [| "-r:" + providerDll |]

            compileWithLogging (Some log) "generic-consumer" consumerArgs (Some (consumerProject, [ consumerPath ]))
            log (sprintf "[fs1023][generic] consumer compiled -> %s" outputDll)

            let loadAssembly path =
                File.ReadAllBytes(path)
                |> Assembly.Load

            let assertProvidedType (assembly: Assembly) typeName =
                let providedType = assembly.GetType(typeName, throwOnError = true, ignoreCase = false)
                Assert.NotNull(providedType)

                let getStaticStringProperty name =
                    let propertyInfo = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(propertyInfo)
                    propertyInfo.GetValue(null) :?> string

                let assertStaticStringProperty name expected =
                    let propertyInfo = providedType.GetProperty(name, BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(propertyInfo)
                    Assert.Equal(typeof<string>, propertyInfo.PropertyType)
                    Assert.Equal(expected, propertyInfo.GetValue(null) :?> string)

                let assertValueProperty () =
                    let valueProp = providedType.GetProperty("Value", BindingFlags.Public ||| BindingFlags.Static)
                    Assert.NotNull(valueProp)
                    Assert.Equal(typeof<string>, valueProp.PropertyType)
                    Assert.Empty(valueProp.GetIndexParameters())
                    Assert.Equal("Value", valueProp.GetValue(null) :?> string)

                assertValueProperty()
                assertStaticStringProperty "MapParameters" "value:required:normal;rest:required:paramarray"
                assertStaticStringProperty "OptionalParameter" "value:true:true:OptionalArgumentAttribute"
                assertStaticStringProperty "OptionalLiteralParameter" "value:true:true:42"
                assertStaticStringProperty "IndexerParameters" "index:Int32"
                assertStaticStringProperty "EventSummary" "ValueChanged"
                assertStaticStringProperty "ModuleTypeSummary" "Model;Provided;UseProvided"
                assertStaticStringProperty "HiddenMethodVisibility" "nonpublic-only"
                assertStaticStringProperty "HiddenPropertyVisibility" "nonpublic-only"

                Assert.Equal("Value", getStaticStringProperty "Value")

            let assertGenericSourceType (assembly: Assembly) =
                let genericTypeDef = assembly.GetType("Fs1023Consumer.Generic`1", throwOnError = true, ignoreCase = false)
                let bindingFlags = BindingFlags.Public ||| BindingFlags.Instance

                let assertForType (ty: Type) =
                    let closed = genericTypeDef.MakeGenericType([| ty |])

                    let valueProp = closed.GetProperty("Value", bindingFlags)
                    Assert.NotNull(valueProp)
                    Assert.Equal(ty, valueProp.PropertyType)
                    Assert.Empty(valueProp.GetIndexParameters())

                    let mapMethod = closed.GetMethod("Map", bindingFlags)
                    let mapParameters = mapMethod.GetParameters()
                    Assert.Equal(2, mapParameters.Length)
                    Assert.Equal(ty, mapParameters[0].ParameterType)
                    let restParam = mapParameters.[1]
                    Assert.Equal(typeof<string[]>, restParam.ParameterType)
                    let hasParamArray =
                        restParam.GetCustomAttributes(typeof<ParamArrayAttribute>, false)
                        |> Array.isEmpty
                        |> not
                    Assert.True(hasParamArray, "Expected rest parameter to carry ParamArrayAttribute.")

                    let optionalMethod = closed.GetMethod("Optional", bindingFlags)
                    let optionalParam = optionalMethod.GetParameters() |> Array.exactlyOne
                    Assert.True(optionalParam.IsOptional, "Expected Optional parameter to be optional.")
                    Assert.Equal(ty, optionalParam.ParameterType)

                    let indexerProp = closed.GetProperty("Item", bindingFlags)
                    Assert.NotNull(indexerProp)
                    let indexParameters = indexerProp.GetIndexParameters()
                    Assert.Equal(1, indexParameters.Length)
                    Assert.Equal(typeof<int>, indexParameters.[0].ParameterType)

                    let optionalLiteralMethod = closed.GetMethod("OptionalLiteral", bindingFlags)
                    let literalParam = optionalLiteralMethod.GetParameters() |> Array.exactlyOne
                    Assert.True(literalParam.IsOptional, "Expected OptionalLiteral parameter to be optional.")
                    Assert.True(literalParam.HasDefaultValue, "Expected OptionalLiteral parameter to expose a default value.")
                    Assert.Equal(42, literalParam.DefaultValue :?> int)
                    Assert.Equal(typeof<int>, literalParam.ParameterType)

                [ typeof<int>; typeof<string> ] |> List.iter assertForType

            let consumerAssembly = loadAssembly outputDll

            assertProvidedType consumerAssembly "Fs1023Consumer.ProvidedGenericInt"
            assertProvidedType consumerAssembly "Fs1023Consumer.ProvidedGenericString"
            assertGenericSourceType consumerAssembly

            // Recompile with /standalone to ensure the IlxGen-emitted IL survives static linking.
            let standaloneDll = Path.Combine(tempDir, "GenericConsumer.standalone.dll")
            let standaloneArgs = Array.append consumerArgs [| "--standalone"; "--out:" + standaloneDll |]

            compileWithLogging (Some log) "generic-consumer-standalone" standaloneArgs (Some (consumerProject, [ consumerPath ]))
            log (sprintf "[fs1023][generic] standalone consumer compiled -> %s" standaloneDll)

            let standaloneAssembly = loadAssembly standaloneDll

            assertProvidedType standaloneAssembly "Fs1023Consumer.ProvidedGenericInt"
            assertProvidedType standaloneAssembly "Fs1023Consumer.ProvidedGenericString"
            assertGenericSourceType standaloneAssembly
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
                Assert.Equal("value:true:true:OptionalArgumentAttribute", getStaticStringProperty "OptionalParameter")
                Assert.Equal("value:true:true:42", getStaticStringProperty "OptionalLiteralParameter")
                Assert.Equal("index:Int32", getStaticStringProperty "IndexerParameters")
                Assert.Equal("ValueChanged", getStaticStringProperty "EventSummary")
                Assert.Equal("Model;Provided;UseProvided", getStaticStringProperty "ModuleTypeSummary")
                Assert.Equal("nonpublic-only", getStaticStringProperty "HiddenMethodVisibility")
                Assert.Equal("nonpublic-only", getStaticStringProperty "HiddenPropertyVisibility")

                let publicInstanceMembers =
                    providedType.GetMembers(BindingFlags.Public ||| BindingFlags.Instance)
                    |> Array.map (fun m -> m.Name)

                Assert.Contains(".ctor", publicInstanceMembers)

                let shapeProvidedType = consumerAssembly.GetType("Fs1023Consumer.ShapeProvided", throwOnError = true, ignoreCase = false)
                let mapMethodProvided =
                    providedType.GetMethod("MapParameters", BindingFlags.Public ||| BindingFlags.Static)
                let mapMethodShape =
                    shapeProvidedType.GetMethod("MapParameters", BindingFlags.Public ||| BindingFlags.Static)
                Assert.NotNull(mapMethodProvided)
                Assert.NotNull(mapMethodShape)
                Assert.NotEqual(mapMethodProvided, mapMethodShape)
                let seen = HashSet<MethodInfo>()
                Assert.True(seen.Add mapMethodProvided)
                Assert.True(seen.Add mapMethodShape)

                let tryGetParameterlessCtor (ty: Type) =
                    ty.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                    |> Array.tryFind (fun ctor -> ctor.GetParameters().Length = 0)

                let ctorProvided =
                    match tryGetParameterlessCtor providedType with
                    | Some ctor -> ctor
                    | None -> null
                let ctorShape =
                    match tryGetParameterlessCtor shapeProvidedType with
                    | Some ctor -> ctor
                    | None -> null
                Assert.NotNull(ctorProvided)
                Assert.NotNull(ctorShape)
                Assert.NotEqual(ctorProvided, ctorShape)
                let ctorSet = HashSet<ConstructorInfo>()
                Assert.True(ctorSet.Add ctorProvided)
                Assert.True(ctorSet.Add ctorShape)

                let hiddenMethodPublic =
                    providedType.GetMethod("HiddenSummary", BindingFlags.Public ||| BindingFlags.Instance)
                Assert.Null(hiddenMethodPublic)

                let hiddenMethodNonPublic =
                    providedType.GetMethod("HiddenSummary", BindingFlags.NonPublic ||| BindingFlags.Instance)
                Assert.NotNull(hiddenMethodNonPublic)

                let hiddenPropertyPublic =
                    providedType.GetProperty("HiddenResult", BindingFlags.Public ||| BindingFlags.Instance)
                Assert.Null(hiddenPropertyPublic)

                let hiddenPropertyNonPublic =
                    providedType.GetProperty("HiddenResult", BindingFlags.NonPublic ||| BindingFlags.Instance)
                Assert.NotNull(hiddenPropertyNonPublic)

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

    [<Fact>]
    let ``json serializer provider roundtrip works`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        try
            let providerPath = Path.Combine(tempDir, "JsonSerializerProvider.fs")
            let providerDll = Path.Combine(tempDir, "Fs1023JsonProvider.dll")

            writeFile providerPath jsonSerializerProviderSource

            let jsonAssemblyPath = typeof<JsonSerializer>.Assembly.Location

            let providerArgs =
                [| yield! mkProjectCommandLineArgs(providerDll, [ providerPath ])
                   yield "-r:" + providedTypesAssembly
                   yield "-r:" + jsonAssemblyPath |]

            compile providerArgs

            let modelPath = Path.Combine(tempDir, "Model.fs")
            let consumerPath = Path.Combine(tempDir, "Consumer.fs")

            writeFile modelPath jsonSerializerModelSource
            writeFile consumerPath jsonSerializerConsumerSource

            let outputDll = Path.Combine(tempDir, "SampleJson.dll")

            let consumerArgs =
                [| yield! mkProjectCommandLineArgs(outputDll, [ modelPath; consumerPath ])
                   yield "-r:" + providerDll
                   yield "-r:" + jsonAssemblyPath |]

            compile consumerArgs

            let consumerAssembly = Assembly.Load(File.ReadAllBytes(outputDll))
            let testsType = consumerAssembly.GetType("SampleJson.Tests", throwOnError = true, ignoreCase = false)
            let roundTripMethod = testsType.GetMethod("roundTrip", BindingFlags.Public ||| BindingFlags.Static)
            Assert.NotNull(roundTripMethod)
            let result = roundTripMethod.Invoke(null, [||]) :?> bool
            Assert.True(result, "Expected JSON serializer provider round trip to succeed.")
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``TypeReflectionBuilder captures dependencies for Fs1023 static arguments`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fs1023-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore

        let rec collectEntities declarations =
            seq {
                for decl in declarations do
                    match decl with
                    | FSharpImplementationFileDeclaration.Entity(entity, nested) ->
                        yield entity
                        yield! collectEntities nested
                    | _ -> ()
            }

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

            let consumerText = SourceText.ofString(File.ReadAllText(consumerPath))
            let _, checkAnswer =
                checkerWithContents.ParseAndCheckFileInProject(consumerPath, 0, consumerText, projectOptions)
                |> Async.RunImmediate

            let checkResults =
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> results
                | FSharpCheckFileAnswer.Aborted -> failwith "Type checking aborted unexpectedly."

            let tcImports =
                match checkResults.TryGetCurrentTcImports() with
                | Some imports -> imports
                | None -> failwith "TcImports were not available from the check results."

            let importMap = tcImports.GetImportMap()

            let modelEntity =
                projectResults.AssemblyContents.ImplementationFiles
                |> Seq.collect (fun impl -> collectEntities impl.Declarations)
                |> Seq.tryFind (fun entity -> entity.FullName = "Fs1023Consumer.Model")
                |> Option.defaultWith (fun () -> failwith "Expected Fs1023Consumer.Model in AssemblyContents.")

            let modelTyconRef = getTyconRef modelEntity
            let projectContext = checkResults.ProjectContext
            let topCcu =
                match ccuOfTyconRef modelTyconRef with
                | Some ccu -> ccu
                | None -> getProjectContextCcu projectContext

            let nonNull = Nullness.Known NullnessInfo.WithoutNull
            let modelTy = TType_app(modelTyconRef, [], nonNull)

            let reflectedType, dependencies = importMap.ReflectTypeWithDependencies(topCcu, modelTy)

            Assert.Equal("Fs1023Consumer.Model", reflectedType.FullName)
            let proxyFromEntity = modelEntity.GetTypeReflectionProxy()
            Assert.Equal("Fs1023Consumer.Model", proxyFromEntity.FullName)
            Assert.True(
                dependencies |> Array.exists (fun dep -> dep.Stamp = modelTyconRef.Stamp),
                "Expected TypeReflectionBuilder to report the Fs1023Consumer.Model dependency.")
        finally
            if Environment.GetEnvironmentVariable("FS1023_KEEP_TEMP") = "1" then
                printfn "[fs1023] preserving temp dir %s" tempDir
            else
                try Directory.Delete(tempDir, true) with _ -> ()

    let private recordInputSource = """
namespace Fs1023Consumer

type RecordInput =
    { Value: int
      Bar: string }
    with
        member recordInput.Summary() = sprintf "%d-%s" recordInput.Value recordInput.Bar
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
                Assert.Equal("value:true:true:OptionalArgumentAttribute", getter "OptionalParameter")
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
