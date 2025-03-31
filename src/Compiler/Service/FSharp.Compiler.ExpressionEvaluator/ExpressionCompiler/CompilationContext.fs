namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Evaluation
open FSharp.Compiler.CodeAnalysis
open System.Collections.Generic

/// Manages the compilation context for expression evaluation
module CompilationContext =
    /// Represents the compilation settings for expression evaluation
    type CompilationSettings =
        {
            /// The F# checker instance
            Checker: FSharpChecker
            /// The current project options
            ProjectOptions: FSharpProjectOptions
            /// The current file name
            FileName: string
            /// The current assembly identities
            AssemblyIdentities: string list
            /// The current diagnostic bag
            Diagnostics: List<string>
        }

    /// Creates a new compilation context
    let createContext (checker: FSharpChecker) (projectOptions: FSharpProjectOptions) (fileName: string) =
        {
            Checker = checker
            ProjectOptions = projectOptions
            FileName = fileName
            AssemblyIdentities = []
            Diagnostics = List<string>()
        }

    /// Adds a diagnostic message to the context
    let addDiagnostic (context: CompilationSettings) (message: string) =
        { context with Diagnostics = context.Diagnostics @ [message] } 