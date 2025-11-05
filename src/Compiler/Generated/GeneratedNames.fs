module internal FSharp.Compiler.GeneratedNames

open System.Text
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax.PrettyNaming

type MethodGeneratedNameInfo =
    { MethodName: string
      MethodOrdinal: int
      MethodGeneration: int }

type EntityGeneratedNameInfo =
    { EntityOrdinal: int
      EntityGeneration: int }

let private methodScopedSuffix (kind: string) (methodInfo: MethodGeneratedNameInfo option) (entityInfo: EntityGeneratedNameInfo option) (extraSegments: string list) =
    let segments = ResizeArray<string>()

    if not (System.String.IsNullOrEmpty kind) then
        segments.Add kind

    match methodInfo with
    | Some info ->
        if info.MethodOrdinal >= 0 then segments.Add(sprintf "m%d" info.MethodOrdinal)
        if info.MethodGeneration >= 0 then segments.Add(sprintf "mg%d" info.MethodGeneration)
    | None -> ()

    match entityInfo with
    | Some entity ->
        if entity.EntityOrdinal >= 0 then segments.Add(sprintf "e%d" entity.EntityOrdinal)
        if entity.EntityGeneration >= 0 then segments.Add(sprintf "eg%d" entity.EntityGeneration)
    | None -> ()

    for segment in extraSegments do
        if not (System.String.IsNullOrEmpty segment) then
            segments.Add segment

    if segments.Count = 0 then "hotreload" else String.concat "_" (Seq.toList segments)

let makeCompilerGeneratedValueName (baseName: string) methodInfo entityInfo =
    let suffix = methodScopedSuffix "" methodInfo entityInfo [ "hotreload" ]
    CompilerGeneratedNameSuffix baseName suffix

let makeStateMachineTypeName (methodInfo: MethodGeneratedNameInfo) =
    let suffix = methodScopedSuffix "statemachine" (Some methodInfo) None [ "state" ]
    CompilerGeneratedNameSuffix methodInfo.MethodName suffix

let makeLambdaClosureTypeName (methodInfo: MethodGeneratedNameInfo) (entityInfo: EntityGeneratedNameInfo option) =
    let suffix = methodScopedSuffix "lambdaClosure" (Some methodInfo) entityInfo [ "display" ]
    CompilerGeneratedNameSuffix methodInfo.MethodName suffix

let makeLambdaMethodName (methodInfo: MethodGeneratedNameInfo) (entityInfo: EntityGeneratedNameInfo option) =
    let suffix = methodScopedSuffix "lambda" (Some methodInfo) entityInfo [ "hotreload" ]
    CompilerGeneratedNameSuffix methodInfo.MethodName suffix

let makeStaticFieldName (baseName: string) (ordinal: int) =
    let builder = StringBuilder()
    builder.Append(baseName).Append("@hotreloadStatic_") |> ignore
    builder.Append(string ordinal) |> ignore
    builder.ToString()

let makeLocalValueName (baseName: string) (m: range) =
    let builder = StringBuilder()
    builder.Append(baseName).Append("@L") |> ignore
    builder
        .Append(string m.StartLine)
        .Append('_')
        .Append(string m.StartColumn)
    |> ignore
    builder.ToString()

let makeHotReloadName (baseName: string) ordinal =
    let suffix =
        if ordinal <= 0 then
            "hotreload"
        else
            sprintf "hotreload-%d" ordinal

    CompilerGeneratedNameSuffix baseName suffix
