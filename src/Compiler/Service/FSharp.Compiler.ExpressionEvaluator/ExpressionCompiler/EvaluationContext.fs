namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.Engine
open FSharp.Compiler.SourceCodeServices

/// Manages the evaluation context for expression evaluation
module EvaluationContext =
    open SharedTypes
    open CompilationContext

    /// Represents the full evaluation context
    type EvaluationContext =
        {
            /// The compilation settings
            CompilationSettings: CompilationSettings
            /// The current evaluation context
            Context: SharedTypes.EvaluationContext
            /// The current scope's type environment
            TypeEnvironment: Map<string, System.Type>
        }

    /// Creates a new evaluation context
    let createContext (compilationSettings: CompilationSettings) (context: SharedTypes.EvaluationContext) =
        {
            CompilationSettings = compilationSettings
            Context = context
            TypeEnvironment = Map.empty
        } 