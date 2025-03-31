namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.Engine

/// Implements the F# formatter for debugger values
type FSharpFormatter() =
    interface IDkmClrFormatter with
        member this.GetValueString(value: obj, context: DkmClrFormatterContext) =
            // TODO: Implement value string formatting
            raise (System.NotImplementedException())

        member this.GetTypeName(value: obj, context: DkmClrFormatterContext) =
            // TODO: Implement type name formatting
            raise (System.NotImplementedException())

        member this.GetValueString(value: obj, context: DkmClrFormatterContext, formatSpecifiers: string) =
            // TODO: Implement value string formatting with format specifiers
            raise (System.NotImplementedException()) 