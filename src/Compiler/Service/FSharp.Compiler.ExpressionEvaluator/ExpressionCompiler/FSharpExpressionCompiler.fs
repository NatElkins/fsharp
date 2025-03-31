namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.Engine
open FSharp.Compiler.SourceCodeServices

/// Implements the F# expression compiler for the debugger
type FSharpExpressionCompiler() =
    interface IDkmClrExpressionCompiler with
        member this.CompileExpression(expression: string, context: DkmClrExpressionCompilerContext) =
            // TODO: Implement expression compilation
            raise (System.NotImplementedException())

        member this.GetClrLocalVariableQuery(context: DkmClrExpressionCompilerContext) =
            // TODO: Implement local variable query
            raise (System.NotImplementedException())

        member this.CompileAssignment(expression: string, context: DkmClrExpressionCompilerContext) =
            // TODO: Implement assignment compilation
            raise (System.NotImplementedException()) 