// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Compiles F# expressions and locals queries for the Visual Studio debugger (Concord).
///
/// The debugger runs the emitted IL "inside" the paused frame: the emitted method's
/// parameters mirror the frame method's `this` and arguments and its local signature
/// mirrors the frame method's local slots, so `ldarg` and `ldloc` read the live values.
namespace FSharp.Compiler.Debugger

open System

/// A local variable the PDB reports as visible at the frame's IL offset.
[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocal =
    {
        /// Source name of the local.
        Name: string
        /// Index into the frame method's local signature.
        Slot: int
    }

/// The debugger's view of the stack frame an expression is evaluated in.
[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerFrame =
    {
        /// Full path of the module that owns the frame method; metadata and IL are read from this file.
        ModulePath: string
        /// Metadata token of the frame method.
        MethodToken: int
        /// IL offset of the frame's current instruction.
        ILOffset: int
        /// Locals visible at the offset, from the PDB local scopes.
        LocalsInScope: FSharpDebuggerLocal list
    }

/// One variable of a locals query and the emitted method that reads it.
[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalEntry =
    { Name: string
      MethodName: string
      IsArgument: bool }

/// The assembly the debugger executes to populate the Locals window.
[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalsQuery =
    { Assembly: byte[]
      TypeName: string
      Locals: FSharpDebuggerLocalEntry list }

/// The assembly the debugger executes to evaluate one expression.
[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerCompiledQuery =
    {
        Assembly: byte[]
        TypeName: string
        MethodName: string
        /// The expression has type bool and can drive a breakpoint condition.
        ResultIsBool: bool
        /// The expression may call code with side effects; the debugger will not evaluate it implicitly.
        HasSideEffects: bool
    }

/// Compiles debugger queries against one debuggee process. Create one per debug session and
/// dispose it when the session ends; module readers are cached for its lifetime.
[<Experimental("This FCS API is experimental and subject to change."); Sealed>]
type FSharpDebuggerExpressionCompiler =
    /// <param name="runtimeModulePath">Path of the debuggee's core library (mscorlib or System.Private.CoreLib).</param>
    /// <param name="referencePaths">Paths of every module loaded in the debuggee's application domain.</param>
    new: runtimeModulePath: string * referencePaths: seq<string> -> FSharpDebuggerExpressionCompiler

    /// Emits one accessor method per argument and, unless <paramref name="argumentsOnly"/> is set, per local in scope.
    member CompileLocalsQuery:
        frame: FSharpDebuggerFrame * argumentsOnly: bool -> Result<FSharpDebuggerLocalsQuery, string>

    /// Compiles <paramref name="expression"/> for evaluation in <paramref name="frame"/>.
    member CompileExpression:
        frame: FSharpDebuggerFrame * expression: string -> Result<FSharpDebuggerCompiledQuery, string>

    interface IDisposable
