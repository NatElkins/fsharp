module internal FSharp.Compiler.HotReloadEmitHook

open System
open System.IO
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.GeneratedNames
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.HotReloadPdb
open FSharp.Compiler.SynthesizedTypeMaps
open FSharp.Compiler.Text.Range

/// Hot reload emit hook implementation used when --enable:hotreloaddeltas is active.
type internal DefaultHotReloadEmitHook() =

    let captureArtifacts
        (compilerGlobalState: CompilerGlobalState)
        (artifacts: CompilerEmitArtifacts)
        =
        let portablePdbSnapshot = artifacts.PortablePdbBytes |> Option.map HotReloadPdb.createSnapshot

        let ilxGenEnvironment =
            if obj.ReferenceEquals(artifacts.IlxGenEnvSnapshot, null) then
                None
            else
                Some artifacts.IlxGenEnvSnapshot

        let baseline =
            HotReloadBaseline.createFromEmittedArtifacts
                artifacts.IlxMainModule
                artifacts.TokenMappings
                artifacts.AssemblyBytes
                portablePdbSnapshot
                ilxGenEnvironment

        FSharpEditAndContinueLanguageService.Instance.StartSession(baseline, artifacts.OptimizedImpls) |> ignore

        match compilerGlobalState.CompilerGeneratedNameMap with
        | Some map -> map.BeginSession()
        | None -> ()

    interface ICompilerEmitHook with
        member _.ValidateConfiguration(emitCaptureArtifacts, debugInfo, localOptimizationsEnabled) =
            if emitCaptureArtifacts then
                if not debugInfo then
                    error (Error(FSComp.SR.fscHotReloadRequiresDebugInfo (), rangeStartup))

                if localOptimizationsEnabled then
                    error (Error(FSComp.SR.fscHotReloadIncompatibleWithOptimization (), rangeStartup))

        member _.PrepareForCodeGeneration(emitCaptureArtifacts, compilerGlobalState) =
            if emitCaptureArtifacts then
                match compilerGlobalState.CompilerGeneratedNameMap with
                | Some map -> map.BeginSession()
                | None ->
                    let map = FSharpSynthesizedTypeMaps()
                    map.BeginSession()
                    compilerGlobalState.CompilerGeneratedNameMap <- Some(map :> ICompilerGeneratedNameMap)
            elif FSharpEditAndContinueLanguageService.Instance.IsSessionActive then
                // Preserve synthesized-name replay while a hot reload session is active,
                // even when the output build itself is emitted without capture flags.
                let activeMap =
                    match compilerGlobalState.CompilerGeneratedNameMap with
                    | Some existing -> Some existing
                    | None ->
                        match FSharpEditAndContinueLanguageService.Instance.TryGetSession() with
                        | ValueSome session ->
                            let restored = FSharpSynthesizedTypeMaps()

                            session.Baseline.SynthesizedNameSnapshot
                            |> Map.toSeq
                            |> Seq.map (fun (k, v) -> struct (k, v))
                            |> restored.LoadSnapshot

                            Some(restored :> ICompilerGeneratedNameMap)
                        | ValueNone -> None

                match activeMap with
                | Some map ->
                    map.BeginSession()
                    compilerGlobalState.CompilerGeneratedNameMap <- Some map
                | None ->
                    compilerGlobalState.CompilerGeneratedNameMap <- None
            else
                compilerGlobalState.CompilerGeneratedNameMap <- None

        member _.BeforeFileEmit(emitCaptureArtifacts, compilerGlobalState) =
            // Only clear the hot reload session when NOT in capture mode.
            // In IDE scenarios, MSBuild may run in the background and we don't want
            // to clear an active hot reload session being used for live editing.
            if not emitCaptureArtifacts then
                FSharpEditAndContinueLanguageService.Instance.EndSession()
                compilerGlobalState.CompilerGeneratedNameMap <- None

        member _.TryEmitWithArtifacts(
            emitCaptureArtifacts,
            compilerGlobalState,
            ilWriteOptions,
            ilxMainModule,
            normalizeAssemblyRefs,
            optimizedImpls,
            ilxGenEnvSnapshot,
            outputFile,
            pdbfile
        ) =
            if not emitCaptureArtifacts then
                false
            else
                let assemblyBytes, pdbBytesOpt, tokenMappings, _ =
                    WriteILBinaryInMemoryWithArtifacts(ilWriteOptions, ilxMainModule, normalizeAssemblyRefs)

                // Emit once in-memory and persist those exact artifacts to disk to avoid
                // a second write pass diverging from the captured baseline input.
                File.WriteAllBytes(outputFile, assemblyBytes)

                match pdbfile, pdbBytesOpt with
                | Some pdbPath, Some pdbBytes -> File.WriteAllBytes(pdbPath, pdbBytes)
                | _ -> ()

                captureArtifacts
                    compilerGlobalState
                    { IlxMainModule = ilxMainModule
                      TokenMappings = tokenMappings
                      AssemblyBytes = assemblyBytes
                      PortablePdbBytes = pdbBytesOpt
                      IlxGenEnvSnapshot = ilxGenEnvSnapshot
                      OptimizedImpls = optimizedImpls }

                true

        member _.CaptureArtifacts(compilerGlobalState, artifacts) =
            captureArtifacts compilerGlobalState artifacts

        member _.FallbackEmit(compilerGlobalState) =
            FSharpEditAndContinueLanguageService.Instance.EndSession()
            compilerGlobalState.CompilerGeneratedNameMap <- None

let hotReloadCompilerEmitHook : ICompilerEmitHook =
    DefaultHotReloadEmitHook() :> ICompilerEmitHook
