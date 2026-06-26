#nowarn "57"
// TEMPORARY hot-reload perf harness. Not for commit. Gated on HRPERF_RUN=1.
// Drives the real FCS hot reload API (CreateHotReloadSession/AddProject/EmitDelta) on an
// external multi-file project and times each phase, so we can measure the effect of each
// short-term perf fix without the dotnet-watch toolset.
namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.Diagnostics
open System.IO
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.CodeAnalysis.TransparentCompiler

module PerfHarness =

    let private env name dflt =
        match Environment.GetEnvironmentVariable(name: string) with
        | null | "" -> dflt
        | v -> v

    let private isOn name =
        match Environment.GetEnvironmentVariable(name: string) with
        | "1" | "true" | "TRUE" -> true
        | _ -> false

    let private capabilities =
        [ "Baseline"; "AddMethodToExistingType"; "AddStaticFieldToExistingType"
          "AddInstanceFieldToExistingType"; "NewTypeDefinition"; "ChangeCustomAttributes"
          "UpdateParameters"; "GenericUpdateMethod"; "GenericAddMethodToExistingType"
          "GenericAddFieldToExistingType"; "AddFieldRva"; "AddExplicitInterfaceImplementation" ]

    let private timeMs (f: unit -> 'a) : 'a * float =
        let sw = Stopwatch.StartNew()
        let r = f ()
        sw.Stop()
        r, sw.Elapsed.TotalMilliseconds

    // external `dotnet build <proj> -t:Compile` (the current SDK per-edit compile)
    let private externalCompile (dotnet: string) (proj: string) =
        let psi = ProcessStartInfo(dotnet, $"build \"{proj}\" -t:Compile -nologo -consoleLoggerParameters:NoSummary;Verbosity=quiet")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.WorkingDirectory <- Path.GetDirectoryName proj
        use p = Process.Start psi
        let o = p.StandardOutput.ReadToEnd()
        let e = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 then failwithf "external build failed (%d): %s %s" p.ExitCode o e

    [<Fact>]
    let ``hot reload per-edit timing harness`` () =
        if not (isOn "HRPERF_RUN") then () else

        let proj = env "HRPERF_PROJ" ""
        let argsFile = env "HRPERF_ARGS" ""
        let outDll = env "HRPERF_OUT" ""
        let editFile = env "HRPERF_EDITFILE" ""
        let compileMode = env "HRPERF_COMPILE" "external"   // external | inproc
        let dotnet = env "HRPERF_DOTNET" "dotnet"
        let iters = env "HRPERF_ITERS" "4" |> int
        let useTC = isOn "HRPERF_TC"
        let reuse = isOn "HRPERF_REUSE"   // S4-style: reuse one snapshot identity, Replace only the edited file
        let logPath = env "HRPERF_LOG" "/tmp/hrperf.log"
        let emit (s: string) =
            printfn "%s" s
            File.AppendAllText(logPath, s + "\n")

        let baseArgs = File.ReadAllLines argsFile |> Array.filter (fun l -> l.Trim() <> "")
        let args = Array.append baseArgs [| $"--out:{outDll}" |]

        let cacheFactor = env "HRPERF_CACHE" "100" |> int
        let checker =
            FSharpChecker.Create(
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = false,
                enablePartialTypeChecking = false,
                useTransparentCompiler = useTC,
                transparentCompilerCacheSizes = CacheSizes.Create cacheFactor)

        // GetProjectOptionsFromCommandLineArgs does NOT populate SourceFiles; set it explicitly
        // to the .fs source args (compile order) like the FCS tests do, else the check is degenerate.
        let sourceFiles =
            baseArgs |> Array.filter (fun a -> not (a.StartsWith "-") && a.EndsWith ".fs")
        let mkSnapshot () =
            let options =
                { checker.GetProjectOptionsFromCommandLineArgs(proj, args) with
                    SourceFiles = sourceFiles }
            FSharpProjectSnapshot.FromOptions(options, DocumentSource.FileSystem) |> Async.RunSynchronously

        let inprocCompile () =
            // HRPERF_SERIAL pins parallelcompilation off (parallel IlxGen / opt are the documented
            // source of non-deterministic synthesized closure/type names, dotnet/fsharp #19732/#19928).
            let extra = if isOn "HRPERF_SERIAL" then [| "--parallelcompilation-" |] else [||]
            let argv = Array.concat [ [| "fsc.exe" |]; args; extra ]
            let _diags, exOpt = checker.Compile(argv) |> Async.RunSynchronously
            match exOpt with Some ex -> raise ex | None -> ()

        let compile () =
            if compileMode = "inproc" then inprocCompile () else externalCompile dotnet proj

        let editOnce (n: int) =
            let txt = File.ReadAllText editFile
            // flip a stable marker so each edit is a real, non-rude change
            let marker = "HRPERF_EDIT_"
            let txt2 =
                if txt.Contains marker then
                    System.Text.RegularExpressions.Regex.Replace(txt, marker + "[0-9]+", marker + string n)
                else
                    txt.Replace("h2 \"Bio\"", $"h2 \"Bio {marker}{n}\"")
            File.WriteAllText(editFile, txt2)

        emit (sprintf "[HRPERF] proj=%s compile=%s transparentCompiler=%b iters=%d" proj compileMode useTC iters)

        // baseline build + AddProject (capture committed baseline)
        let (), tBuild0 = timeMs compile
        let session = checker.CreateHotReloadSession(capabilities)
        let snap0 = mkSnapshot ()
        emit (sprintf "[HRPERF] snapshot SourceFiles=%d" snap0.SourceFiles.Length)
        if not (isOn "HRPERF_NOSESSION") then
            let _add, tAdd = timeMs (fun () ->
                session.AddProject(snap0, outputPath = outDll) |> Async.RunSynchronously)
            emit (sprintf "[HRPERF] baseline: build=%.0fms addProject=%.0fms" tBuild0 tAdd)

        let pvName =
            snap0.SourceFiles
            |> Seq.tryFind (fun f -> f.FileName.Contains "ProfileView")
            |> Option.map (fun f -> f.FileName)
            |> Option.defaultValue editFile

        for i in 1 .. iters do
            if not (isOn "HRPERF_NOEDIT") then editOnce i
            if isOn "HRPERF_CLEARCACHE" then checker.InvalidateAll()

            let mkSnap () =
                if reuse then
                    // S4 shape: same project identity, only the edited file's snapshot replaced.
                    let pv = FSharpFileSnapshot.CreateFromString(pvName, File.ReadAllText editFile)
                    snap0.Replace [ pv ]
                else
                    mkSnapshot ()

            if isOn "HRPERF_PARALLEL" && not (isOn "HRPERF_NOSESSION") then
                // Overlap compile #1 (external build, produces the obj DLL) with compile #2 (the
                // in-process check). Warm the TransparentCompiler cache for this snapshot while the
                // build runs; EmitDelta's own check is then a cache hit and the DLL read sees the
                // finished build.
                let sw = Stopwatch.StartNew()
                let snap = mkSnap ()
                let buildTask = System.Threading.Tasks.Task.Run(System.Action(fun () -> compile ()))
                let warmTask =
                    System.Threading.Tasks.Task.Run(System.Action(fun () -> checker.ParseAndCheckProject(snap) |> Async.RunSynchronously |> ignore))
                System.Threading.Tasks.Task.WaitAll(buildTask, warmTask)
                let res = session.EmitDelta(snap) |> Async.RunSynchronously
                session.Commit()
                sw.Stop()
                let o =
                    match res with
                    | Ok d -> sprintf "Ok updatedMethods=%d" d.UpdatedMethods.Length
                    | Error e -> let s = sprintf "Error %A" e in (if s.Length > 60 then s.Substring(0, 60) else s)
                emit (sprintf "[HRPERF] edit %d: PARALLEL total=%.0fms outcome=%s" i sw.Elapsed.TotalMilliseconds o)
            else
                let (), tCompile = timeMs compile
                let snap = mkSnap ()
                let outcome, tEmit =
                    if isOn "HRPERF_NOSESSION" then
                        // Isolation: pure repeated typecheck, no hot-reload session at all.
                        let chk =
                            if isOn "HRPERF_FRESHCHECKER" then
                                FSharpChecker.Create(
                                    keepAssemblyContents = true,
                                    keepAllBackgroundResolutions = false,
                                    enablePartialTypeChecking = false,
                                    useTransparentCompiler = useTC,
                                    transparentCompilerCacheSizes = CacheSizes.Create cacheFactor)
                            else checker
                        let _r, t = timeMs (fun () -> chk.ParseAndCheckProject(snap) |> Async.RunSynchronously)
                        "noSession-check", t
                    else
                        let res, t = timeMs (fun () -> session.EmitDelta(snap) |> Async.RunSynchronously)
                        session.Commit()
                        let o =
                            match res with
                            | Ok delta -> sprintf "Ok updatedMethods=%d updatedTypes=%d" delta.UpdatedMethods.Length delta.UpdatedTypes.Length
                            | Error e -> let s = sprintf "Error %A" e in (if s.Length > 70 then s.Substring(0, 70) else s)
                        o, t
                let asmCount = System.AppDomain.CurrentDomain.GetAssemblies().Length
                emit (sprintf "[HRPERF] edit %d: compile1=%.0fms emitDelta=%.0fms total=%.0fms asm=%d outcome=%s" i tCompile tEmit (tCompile + tEmit) asmCount outcome)

        if isOn "HRPERF_PAUSE" then
            System.GC.Collect()
            System.GC.WaitForPendingFinalizers()
            System.GC.Collect()
            System.IO.File.WriteAllText("/tmp/hrpid", string (System.Diagnostics.Process.GetCurrentProcess().Id))
            emit (sprintf "[HRPERF] PAUSED pid=%d liveMB=%d" (System.Diagnostics.Process.GetCurrentProcess().Id) (System.GC.GetTotalMemory(true) / 1024L / 1024L))
            System.Threading.Thread.Sleep(150000)
