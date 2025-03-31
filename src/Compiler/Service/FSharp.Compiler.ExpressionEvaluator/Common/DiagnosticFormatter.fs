namespace FSharp.Compiler.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Evaluation
open System
open System.Globalization

/// Provides formatting for diagnostic messages and errors
module DiagnosticFormatter =
    /// Formats a diagnostic message for display in the debugger
    let formatDiagnostic (message: string) (line: int) (column: int) =
        sprintf "Error at line %d, column %d: %s" line column message

    /// Creates a diagnostic message for the debugger
    let createDiagnostic (message: string) (line: int) (column: int) =
        new DkmCustomMessage(
            Message = formatDiagnostic message line column,
            Severity = DkmCustomMessageSeverity.Error
        )

    /// Formats a diagnostic message with culture-specific formatting
    let formatDiagnosticWithCulture (message: string) (culture: CultureInfo) =
        String.Format(culture, "{0}: {1}", "Error", message)

    /// The singleton instance of the diagnostic formatter
    let Instance = DiagnosticFormatter() 