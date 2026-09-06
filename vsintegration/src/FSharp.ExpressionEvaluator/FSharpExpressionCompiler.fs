// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.ExpressionEvaluator

open System
open System.Collections.ObjectModel
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open FSharp.Compiler.Debugger

/// One FCS compiler per debuggee runtime; the debugger closes it with the runtime instance.
/// AllowNullLiteral because DkmClrRuntimeInstance.GetDataItem returns null when nothing is stored yet.
[<AllowNullLiteral>]
type internal CompilerDataItem(compiler: FSharpDebuggerExpressionCompiler) =
    inherit DkmDataItem()

    member _.Compiler = compiler

    override _.OnClose() = (compiler :> IDisposable).Dispose()

module internal Frames =

    let private isRuntimeModule (m: DkmClrModuleInstance) =
        m.ClrFlags.HasFlag DkmClrModuleFlags.RuntimeModule

    /// The compiler for the runtime that owns the address, created on first use.
    let compilerFor (address: DkmClrInstructionAddress) =
        let runtime = address.RuntimeInstance

        match runtime.GetDataItem<CompilerDataItem>() with
        | null ->
            let modules =
                address.ModuleInstance.AppDomain.GetClrModuleInstances()
                |> Array.filter (fun m -> not m.IsUnloaded)

            let runtimeModule =
                match modules |> Array.tryFind isRuntimeModule with
                | Some m -> m
                | None -> invalidOp "The debuggee's runtime module is not loaded yet."

            let compiler =
                new FSharpDebuggerExpressionCompiler(runtimeModule.FullName, modules |> Array.map (fun m -> m.FullName))

            runtime.SetDataItem(DkmDataCreationDisposition.CreateNew, CompilerDataItem compiler)
            compiler
        | item -> item.Compiler

    /// Locals the PDB places in scope at the address, then the frame identity FCS needs.
    let frameOf (address: DkmClrInstructionAddress) : FSharpDebuggerFrame =
        let offset = address.ILOffset

        let locals =
            match address.ModuleInstance.Module with
            | null -> []
            | symbols ->
                symbols.GetMethodSymbolStoreData address.MethodId
                |> Seq.filter (fun scope -> scope.ILRange.StartOffset <= offset && offset <= scope.ILRange.EndOffset)
                |> Seq.collect (fun scope -> scope.LocalVariables)
                |> Seq.map (fun local -> { Name = local.Name; Slot = local.Slot })
                |> List.ofSeq

        {
            ModulePath = address.ModuleInstance.FullName
            MethodToken = address.MethodId.Token
            ILOffset = int offset
            LocalsInScope = locals
        }

/// Concord expression compiler for F# frames. Registered through FSharp.ExpressionEvaluator.vsdconfigxml
/// with a filter on the F# language id, so it takes precedence over Roslyn's catch-all C# compiler.
type FSharpExpressionCompiler() =

    interface IDkmClrExpressionCompiler with

        member _.CompileExpression
            (
                expression: DkmLanguageExpression,
                instructionAddress: DkmClrInstructionAddress,
                _inspectionContext: DkmInspectionContext,
                error: byref<string>,
                result: byref<DkmCompiledClrInspectionQuery>
            ) =
            error <- null
            result <- null

            try
                let compiler = Frames.compilerFor instructionAddress

                match compiler.CompileExpression(Frames.frameOf instructionAddress, expression.Text) with
                | Ok query ->
                    let flags =
                        DkmClrCompilationResultFlags.ReadOnlyResult
                        ||| (if query.ResultIsBool then
                                 DkmClrCompilationResultFlags.BoolResult
                             else
                                 DkmClrCompilationResultFlags.None)
                        ||| (if query.HasSideEffects then
                                 DkmClrCompilationResultFlags.PotentialSideEffect
                             else
                                 DkmClrCompilationResultFlags.None)

                    result <-
                        DkmCompiledClrInspectionQuery.Create(
                            instructionAddress.RuntimeInstance,
                            null,
                            expression.Language.Id,
                            ReadOnlyCollection<byte>(query.Assembly),
                            query.TypeName,
                            query.MethodName,
                            ReadOnlyCollection<string>([||]),
                            flags,
                            DkmEvaluationResultCategory.Data,
                            DkmEvaluationResultAccessType.None,
                            DkmEvaluationResultStorageType.None,
                            DkmEvaluationResultTypeModifierFlags.None,
                            null
                        )
                | Error message -> error <- message
            with ex ->
                error <- $"F# expression evaluator failure: {ex.Message}"

        member _.GetClrLocalVariableQuery
            (
                inspectionContext: DkmInspectionContext,
                instructionAddress: DkmClrInstructionAddress,
                argumentsOnly: bool
            ) =
            let compiler = Frames.compilerFor instructionAddress

            match compiler.CompileLocalsQuery(Frames.frameOf instructionAddress, argumentsOnly) with
            | Ok query ->
                let locals =
                    query.Locals
                    |> List.map (fun local ->
                        DkmClrLocalVariableInfo.Create(
                            local.Name,
                            local.Name,
                            local.MethodName,
                            DkmClrCompilationResultFlags.None,
                            DkmEvaluationResultCategory.Data,
                            null
                        ))
                    |> Array.ofList

                DkmCompiledClrLocalsQuery.Create(
                    inspectionContext.RuntimeInstance,
                    null,
                    inspectionContext.Language.Id,
                    ReadOnlyCollection<byte>(query.Assembly),
                    query.TypeName,
                    ReadOnlyCollection<DkmClrLocalVariableInfo>(locals)
                )
            | Error message -> invalidOp message

        member _.CompileAssignment
            (
                _expression: DkmLanguageExpression,
                _instructionAddress: DkmClrInstructionAddress,
                _lValue: DkmEvaluationResult,
                error: byref<string>,
                result: byref<DkmCompiledClrInspectionQuery>
            ) =
            error <- "The F# expression evaluator does not support assignment yet."
            result <- null
