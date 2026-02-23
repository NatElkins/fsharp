module internal FSharp.Compiler.HotReloadEmitHook

open System
open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.GeneratedNames
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.HotReloadPdb
open FSharp.Compiler.SynthesizedTypeMaps

/// Default hot reload hook used by compiler entry points when no explicit hook is provided.
type internal DefaultHotReloadEmitHook() =
    interface IHotReloadEmitHook with
        member _.PrepareForCodeGeneration(hotReloadCapture, compilerGlobalState) =
            if hotReloadCapture then
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

        member _.BeforeFileEmit(hotReloadCapture, compilerGlobalState) =
            // Only clear the hot reload session when NOT in hot reload capture mode.
            // In IDE scenarios, MSBuild may run in the background and we don't want
            // to clear an active hot reload session being used for live editing.
            if not hotReloadCapture then
                FSharpEditAndContinueLanguageService.Instance.EndSession()
                compilerGlobalState.CompilerGeneratedNameMap <- None

        member _.CaptureArtifacts(compilerGlobalState, artifacts) =
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

            FSharpEditAndContinueLanguageService.Instance.StartSession(baseline, artifacts.OptimizedImpls)

            match compilerGlobalState.CompilerGeneratedNameMap with
            | Some map -> map.BeginSession()
            | None -> ()

        member _.FallbackEmit(compilerGlobalState) =
            FSharpEditAndContinueLanguageService.Instance.EndSession()
            compilerGlobalState.CompilerGeneratedNameMap <- None

let defaultHotReloadEmitHook : IHotReloadEmitHook =
    DefaultHotReloadEmitHook() :> IHotReloadEmitHook
