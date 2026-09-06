// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// The debugger runs a query method in place of the frame method, so the accessors must take the
/// frame's arguments as parameters and declare the frame's locals. Invoking them directly through
/// reflection therefore reproduces what the debugger does with live frame values.
module Debugger.ExpressionEvaluatorTests

#nowarn "57" // the FCS debugger API is experimental on purpose

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Xunit
open FSharp.Test
open FSharp.Test.Compiler
open FSharp.Compiler.Debugger

let private source =
    """
module Sample

type Holder(seed: int) =
    member _.Add(delta: int) =
        let total = seed + delta
        total * 2

let target (a: int) (s: string) =
    let x = a + 1
    let y = s.Length
    x + y
"""

let private compileSample () =
    let result =
        FSharp source
        |> asLibrary
        |> withName "DebuggerSample"
        |> withNoOptimize
        |> withDebug
        |> withPortablePdb
        |> compile
        |> shouldSucceed

    match result.OutputPath with
    | Some path -> path
    | None -> failwith "The compiled sample has no output path."

/// Metadata token of the first method with the given name declared on the given type.
let private methodToken (assemblyPath: string) (typeName: string) (methodName: string) =
    use stream = File.OpenRead assemblyPath
    use pe = new PEReader(stream)
    let md = pe.GetMetadataReader()

    md.MethodDefinitions
    |> Seq.pick (fun handle ->
        let def = md.GetMethodDefinition handle
        let owner = md.GetTypeDefinition(def.GetDeclaringType())

        if md.GetString def.Name = methodName && md.GetString owner.Name = typeName then
            let entity: EntityHandle = MethodDefinitionHandle.op_Implicit handle
            Some(0x06000000 ||| MetadataTokens.GetRowNumber entity)
        else
            None)

let private runtimeModulePath = typeof<obj>.Assembly.Location

let private referencePaths (samplePath: string) =
    [
        yield! Directory.EnumerateFiles(Path.GetDirectoryName runtimeModulePath, "*.dll")
        yield typeof<int list>.Assembly.Location
        yield samplePath
    ]

let private queryMethod (assembly: byte[]) (typeName: string) (methodName: string) =
    let loaded = Assembly.Load assembly
    loaded.GetType(typeName).GetMethod(methodName)

let private targetFrame (path: string) =
    {
        ModulePath = path
        MethodToken = methodToken path "Sample" "target"
        ILOffset = 0
        LocalsInScope = [ { Name = "x"; Slot = 0 }; { Name = "y"; Slot = 1 } ]
    }

let private evaluate (compiler: FSharpDebuggerExpressionCompiler) frame (frameArgs: obj[]) (expression: string) =
    match compiler.CompileExpression(frame, expression) with
    | Result.Error message -> failwith message
    | Result.Ok query -> query, (queryMethod query.Assembly query.TypeName query.MethodName).Invoke(null, frameArgs)

[<Fact>]
let ``Locals query exposes arguments then locals, and accessors read the frame values`` () =
    let path = compileSample ()
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)
    let frame = targetFrame path

    match compiler.CompileLocalsQuery(frame, false) with
    | Result.Error message -> failwith message
    | Result.Ok query ->
        Assert.Equal<string list>([ "a"; "s"; "x"; "y" ], query.Locals |> List.map (fun l -> l.Name))
        Assert.Equal<bool list>([ true; true; false; false ], query.Locals |> List.map (fun l -> l.IsArgument))

        let frameArgs = [| box 41; box "hi" |]
        Assert.Equal(box 41, (queryMethod query.Assembly query.TypeName query.Locals[0].MethodName).Invoke(null, frameArgs))
        Assert.Equal(box "hi", (queryMethod query.Assembly query.TypeName query.Locals[1].MethodName).Invoke(null, frameArgs))

[<Fact>]
let ``Locals query of an instance frame reports this, arguments and the fields of this`` () =
    let path = compileSample ()
    let sample = Assembly.LoadFrom path
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

    let frame =
        {
            ModulePath = path
            MethodToken = methodToken path "Holder" "Add"
            ILOffset = 0
            LocalsInScope = [ { Name = "total"; Slot = 0 } ]
        }

    match compiler.CompileLocalsQuery(frame, false) with
    | Result.Error message -> failwith message
    | Result.Ok query ->
        Assert.Equal<string list>([ "this"; "delta"; "total"; "seed" ], query.Locals |> List.map (fun l -> l.Name))

        let holder = Activator.CreateInstance(sample.GetType("Sample+Holder"), [| box 5 |])
        let frameArgs = [| holder; box 3 |]
        let accessorOf index = queryMethod query.Assembly query.TypeName query.Locals[index].MethodName
        Assert.Same(holder, (accessorOf 0).Invoke(null, frameArgs))
        Assert.Equal(box 5, (accessorOf 3).Invoke(null, frameArgs))

[<Fact>]
let ``Expressions over arguments compile to a frame-shaped method and evaluate`` () =
    let path = compileSample ()
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)
    let frame = targetFrame path
    let frameArgs = [| box 41; box "hi" |]

    let query, value = evaluate compiler frame frameArgs "a + 1"
    Assert.False query.ResultIsBool
    Assert.Equal(box 42, value)

    let _, value = evaluate compiler frame frameArgs "s.Length * 2"
    Assert.Equal(box 4, value)

    let _, value = evaluate compiler frame frameArgs "[ a; a + 1 ] |> List.map (fun v -> v * 2) |> List.sum"
    Assert.Equal(box 166, value)

[<Fact>]
let ``Boolean expressions are flagged as breakpoint conditions`` () =
    let path = compileSample ()
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

    let query, value = evaluate compiler (targetFrame path) [| box 41; box "hi" |] "s = \"hi\" && a > 40"
    Assert.True query.ResultIsBool
    Assert.Equal(box true, value)

[<Fact>]
let ``Type errors are reported with the checker's message`` () =
    let path = compileSample ()
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

    match compiler.CompileExpression(targetFrame path, "a + \"oops\"") with
    | Result.Ok _ -> failwith "Expected a type error."
    | Result.Error message -> Assert.Contains("string", message)

[<Fact>]
let ``Fields of this are visible by name and methods of this are callable`` () =
    let path = compileSample ()
    let sample = Assembly.LoadFrom path
    use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

    let frame =
        {
            ModulePath = path
            MethodToken = methodToken path "Holder" "Add"
            ILOffset = 0
            LocalsInScope = [ { Name = "total"; Slot = 0 } ]
        }

    let holder = Activator.CreateInstance(sample.GetType("Sample+Holder"), [| box 5 |])
    let frameArgs = [| holder; box 3 |]

    let _, value = evaluate compiler frame frameArgs "this.Add(10)"
    Assert.Equal(box 30, value)

    let _, value = evaluate compiler frame frameArgs "seed + delta"
    Assert.Equal(box 8, value)
