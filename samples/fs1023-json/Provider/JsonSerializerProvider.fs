namespace Fs1023Json

open System
open System.Text.Json
open System.IO

namespace Fs1023Json.ProviderImplementation

open System
open System.Reflection
open System.Text.Json
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Fs1023Json
open System.IO
open System.Runtime.ExceptionServices
open System.Threading

[<TypeProvider>]
type JsonSerializerProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let assembly = Assembly.GetExecutingAssembly()
    let namespaceName = "Fs1023Json"
    let parameters = [ ProvidedStaticParameter("Source", typeof<Type>) ]

    let log message =
        let timestamp = DateTime.UtcNow.ToString("O")
        let line = sprintf "%s [fs1023][json-provider] %s%s" timestamp message Environment.NewLine
        let target = Environment.GetEnvironmentVariable("FS1023_SAMPLE_LOG")
        if String.IsNullOrEmpty target then
            printf "%s" line
        else
            File.AppendAllText(target, line)

    let diagnosticsEnabled =
        match Environment.GetEnvironmentVariable("FS1023_SAMPLE_LOG") with
        | null | "" -> false
        | _ -> true

    let mutable firstChanceCount = 0

    do
        if diagnosticsEnabled then
            AppDomain.CurrentDomain.FirstChanceException.Add(fun args ->
                let ex = args.Exception
                let index = Interlocked.Increment(&firstChanceCount)
                if index <= 200 then
                    log (sprintf "first-chance %s" (ex.ToString()))
            )

    let buildType typeName (args: obj[]) =
        try
            log (sprintf "buildType start typeName=%s args=%A" typeName args)
            let sourceType =
                match args with
                | [| :? Type as ty |] when not (isNull ty) -> ty
                | [| null |] -> failwith "Static parameter 'Source' evaluated to null"
                | _ -> failwithf "Unexpected static argument payload: %A" args

            log (sprintf "buildType typeName=%s source=%s" typeName sourceType.FullName)

            let provided =
                ProvidedTypeDefinition(
                    assembly,
                    namespaceName,
                    typeName,
                    baseType = Some typeof<obj>,
                    isErased = false,
                    hideObjectMethods = true
                )

            let typeGetMethod =
                typeof<Type>.GetMethod(
                    "GetType",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<string>; typeof<bool> |],
                    null
                )

            let sourceTypeName = sourceType.AssemblyQualifiedName

            let resolveSourceTypeExpr () =
                Expr.Call(typeGetMethod, [ Expr.Value(sourceTypeName); Expr.Value(true) ])

            let serializeMethod =
                typeof<JsonSerializer>.GetMethod(
                    "Serialize",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<obj>; typeof<Type>; typeof<JsonSerializerOptions> |],
                    null
                )

            let deserializeMethod =
                typeof<JsonSerializer>.GetMethod(
                    "Deserialize",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<string>; typeof<Type>; typeof<JsonSerializerOptions> |],
                    null
                )

            let toJson =
                ProvidedMethod(
                    "ToJson",
                    parameters = [ ProvidedParameter("value", sourceType) ],
                    returnType = typeof<string>,
                    isStatic = true,
                    invokeCode = fun args ->
                        if args.Length <> 1 then
                            failwithf "ToJson expected 1 arg, got %d" args.Length
                        let valueExpr = Expr.Coerce(args.[0], typeof<obj>)
                        Expr.Call(
                            serializeMethod,
                            [ valueExpr; resolveSourceTypeExpr(); Expr.Value(null, typeof<JsonSerializerOptions>) ]
                        )
                )

            provided.AddMember toJson

            let fromJson =
                ProvidedMethod(
                    "FromJson",
                    parameters = [ ProvidedParameter("json", typeof<string>) ],
                    returnType = sourceType,
                    isStatic = true,
                    invokeCode = fun args ->
                        if args.Length <> 1 then
                            failwithf "FromJson expected 1 arg, got %d" args.Length
                        let jsonArg = Expr.Coerce(args.[0], typeof<string>)
                        let deserialized =
                            Expr.Call(
                                deserializeMethod,
                                [ jsonArg; resolveSourceTypeExpr(); Expr.Value(null, typeof<JsonSerializerOptions>) ]
                            )
                        Expr.Coerce(deserialized, sourceType)
                )

            provided.AddMember fromJson
            log (sprintf "buildType completed typeName=%s" typeName)
            provided
        with ex ->
            log (sprintf "buildType fail typeName=%s ex=%s" typeName (ex.ToString()))
            reraise()

    let root =
        ProvidedTypeDefinition(
            assembly,
            namespaceName,
            "JsonSerializerProvider",
            baseType = Some typeof<obj>,
            isErased = false
        )

    do
        log "registering root"
        root.DefineStaticParameters(parameters, buildType)
        this.AddNamespace(namespaceName, [ root ])

[<assembly:TypeProviderAssembly>]
do ()
