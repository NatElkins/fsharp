namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Evaluation
open FSharp.Compiler.CodeAnalysis

/// Common types and utilities shared across the expression evaluator
module SharedTypes =
    /// Represents the result of an expression evaluation
    type EvaluationResult =
        | Success of obj
        | Error of string

    /// Represents the context in which an expression is being evaluated
    type EvaluationContext =
        {
            /// The current method being executed
            CurrentMethod: string
            /// The current line number
            CurrentLine: int
            /// The current column number
            CurrentColumn: int
            /// The current scope's local variables
            LocalVariables: (string * obj) list
            /// The current IL offset
            ILOffset: uint32
            /// The current assembly identities
            AssemblyIdentities: string list
        }

    /// Normalizes an IL offset (0xffffffff indicates an instruction outside of IL)
    let normalizeILOffset (ilOffset: uint32) =
        if ilOffset = System.UInt32.MaxValue then 0u else ilOffset 