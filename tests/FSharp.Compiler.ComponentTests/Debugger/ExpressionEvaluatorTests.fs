// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Debugger

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

/// The debugger runs a query method in place of the frame method, so the accessors must take the
/// frame's arguments as parameters and declare the frame's locals. Invoking them directly through
/// reflection therefore reproduces what the debugger does with live frame values.
module ExpressionEvaluatorTests =

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

    [<Fact>]
    let ``Locals query exposes arguments then locals, and accessors read the frame values`` () =
        let path = compileSample ()
        use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

        let frame =
            {
                ModulePath = path
                MethodToken = methodToken path "Sample" "target"
                ILOffset = 0
                LocalsInScope = [ { Name = "x"; Slot = 0 }; { Name = "y"; Slot = 1 } ]
            }

        match compiler.CompileLocalsQuery(frame, false) with
        | Error message -> failwith message
        | Ok query ->
            Assert.Equal<string list>([ "a"; "s"; "x"; "y" ], query.Locals |> List.map (fun l -> l.Name))
            Assert.Equal<bool list>([ true; true; false; false ], query.Locals |> List.map (fun l -> l.IsArgument))

            let frameArgs = [| box 41; box "hi" |]
            Assert.Equal(box 41, (queryMethod query.Assembly query.TypeName query.Locals[0].MethodName).Invoke(null, frameArgs))
            Assert.Equal(box "hi", (queryMethod query.Assembly query.TypeName query.Locals[1].MethodName).Invoke(null, frameArgs))

    [<Fact>]
    let ``Locals query of an instance frame reports this as argument 0`` () =
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

        match compiler.CompileLocalsQuery(frame, true) with
        | Error message -> failwith message
        | Ok query ->
            Assert.Equal<string list>([ "this"; "delta" ], query.Locals |> List.map (fun l -> l.Name))

            let holder = Activator.CreateInstance(sample.GetType("Sample+Holder"), [| box 5 |])
            let accessor = queryMethod query.Assembly query.TypeName query.Locals[0].MethodName
            Assert.Same(holder, accessor.Invoke(null, [| holder; box 3 |]))

    [<Fact>]
    let ``Integer literal compiles to a frame-shaped method returning the value`` () =
        let path = compileSample ()
        use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

        let frame =
            {
                ModulePath = path
                MethodToken = methodToken path "Sample" "target"
                ILOffset = 0
                LocalsInScope = []
            }

        match compiler.CompileExpression(frame, " 42 ") with
        | Error message -> failwith message
        | Ok query ->
            Assert.False query.ResultIsBool
            Assert.False query.HasSideEffects
            Assert.Equal(box 42, (queryMethod query.Assembly query.TypeName query.MethodName).Invoke(null, [| box 1; box "" |]))

    [<Fact>]
    let ``Expressions beyond the prototype report the limitation instead of failing`` () =
        let path = compileSample ()
        use compiler = new FSharpDebuggerExpressionCompiler(runtimeModulePath, referencePaths path)

        let frame =
            {
                ModulePath = path
                MethodToken = methodToken path "Sample" "target"
                ILOffset = 0
                LocalsInScope = []
            }

        match compiler.CompileExpression(frame, "a + 1") with
        | Ok _ -> failwith "Expected the prototype limitation to be reported."
        | Error message -> Assert.Contains("integer literals", message)
