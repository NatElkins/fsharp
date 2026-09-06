// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Compiler.Debugger

#nowarn "57" // FSharpChecker.Create's transparent-compiler switch is experimental by design

open System
open System.Collections.Generic
open System.IO
open System.Text
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.CheckExpressionsOps
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.CompilerImports
open FSharp.Compiler.CreateILModule
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Import
open FSharp.Compiler.IlxGen
open FSharp.Compiler.OptimizeInputs
open FSharp.Compiler.Syntax
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeOps

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocal = { Name: string; Slot: int }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerFrame =
    {
        ModulePath: string
        MethodToken: int
        ILOffset: int
        LocalsInScope: FSharpDebuggerLocal list
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalEntry =
    {
        Name: string
        MethodName: string
        IsArgument: bool
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalsQuery =
    {
        Assembly: byte[]
        TypeName: string
        Locals: FSharpDebuggerLocalEntry list
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerCompiledQuery =
    {
        Assembly: byte[]
        TypeName: string
        MethodName: string
        ResultIsBool: bool
        HasSideEffects: bool
    }

/// Checking state shared by every query against one frame module: the reference set the
/// checker keeps warm, and the globals and import map that print IL types as F# source.
type internal CheckSession =
    {
        References: string list
        /// Assembly names of the references; query assemblies ignore access checks to all of them.
        AssemblyNames: string list
        TargetProfile: string
        Globals: TcGlobals
        ImportMap: ImportMap
    }

/// Frame data resolved once per method and locals set: the shape, the variables the checker
/// accepts as typed parameters (with their type text), and the `open`s that survived probing.
type internal FrameContext =
    {
        Shape: FrameShape
        Variables: FrameVariable[]
        TypeTexts: string[]
        Opens: string list
    }

module internal DebuggerCheck =

    let queryModuleName = "<>x"
    let queryMethodName = "<>m0"

    let private ident (name: string) =
        PrettyNaming.NormalizeIdentifierBackticks name

    let snapshot (projectName: string) (references: string list) (targetProfile: string) (source: string) : FSharpProjectSnapshot =
        let output = projectName + ".dll"
        let projectFile = projectName + ".fsproj"

        let sourceFiles =
            [ FSharpFileSnapshot.CreateFromString(projectName + ".fs", source) ]

        let referencesOnDisk =
            references
            |> List.map (fun path ->
                {
                    Path = path
                    LastModified = File.GetLastWriteTimeUtc path
                })

        let otherOptions =
            [
                "--noframework"
                "--optimize-"
                "--debug-"
                "--nointerfacedata"
                "--nooptimizationdata"
                "--target:library"
                $"--targetprofile:{targetProfile}"
                "-o:" + output
            ]

        FSharpProjectSnapshot.Create(
            projectFileName = projectFile,
            outputFileName = Some output,
            projectId = None,
            sourceFiles = sourceFiles,
            referencesOnDisk = referencesOnDisk,
            otherOptions = otherOptions,
            referencedProjects = [],
            isIncompleteTypeCheckEnvironment = false,
            useScriptResolutionRules = false,
            loadTime = DateTime.UtcNow,
            unresolvedReferences = None,
            originalLoadReferences = [],
            stamp = None
        )

    let errors (results: FSharpCheckProjectResults) =
        results.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

    let errorText (diagnostics: FSharpDiagnostic[]) =
        String.Join("; ", diagnostics |> Array.map (fun d -> d.Message) |> Array.distinct)

    /// Checks an empty module against the reference set so the checker caches its imports, and
    /// captures the globals and import map that later print IL types as F# source.
    let warmUp (checker: FSharpChecker) (references: string list) (assemblyNames: string list) (targetProfile: string) =
        let warmupName = ident "<>warmup"

        let results =
            checker.ParseAndCheckProject(snapshot "<>warmup" references targetProfile $"module {warmupName}")
            |> Async.RunSynchronously

        match errors results with
        | [||] ->
            let _, tcGlobals, tcImports, _, _, _, _, _ = results.CompilationData

            Ok
                {
                    References = references
                    AssemblyNames = assemblyNames
                    TargetProfile = targetProfile
                    Globals = tcGlobals
                    ImportMap = tcImports.GetImportMap()
                }
        | diagnostics -> Error $"The debuggee's references could not be loaded: {errorText diagnostics}"

    /// The F# source spelling of an IL type, or ValueNone when the type cannot appear in source
    /// (managed pointers, type parameters, `unit` arguments, types the importer rejects).
    let typeText (session: CheckSession) (ty: ILType) =
        match ty with
        | ILType.Byref _
        | ILType.Ptr _
        | ILType.FunctionPointer _
        | ILType.TypeVar _
        | ILType.Modified _
        | ILType.Void -> ValueNone
        | _ when ty.IsNominal && ty.TypeRef.FullName = "Microsoft.FSharp.Core.Unit" -> ValueNone
        | _ when not (CanImportILType session.ImportMap range0 ty) -> ValueNone
        | _ ->
            let denv =
                { DisplayEnv.Empty session.Globals with
                    shortTypeNames = false
                    escapeKeywordNames = true
                    includeStaticParametersInTypeNames = true
                }

            try
                ValueSome(NicePrint.stringOfTy denv (ImportILType session.ImportMap range0 [] ty))
            with _ ->
                ValueNone

    let private appendOpens (sb: StringBuilder) (opens: string list) =
        for path in opens do
            sb.AppendLine($"open {path}") |> ignore

    /// One typed `let` per candidate variable: a check error on a line rejects that variable or `open`.
    let probeSource (opens: string list) (typeTexts: string[]) =
        let sb = StringBuilder()
        let probeName = ident "<>probe"
        sb.AppendLine($"module {probeName}") |> ignore
        appendOpens sb opens

        typeTexts
        |> Array.iteri (fun i text ->
            let probeVariable = ident $"<>p{i}"

            sb.AppendLine($"let {probeVariable} : {text} = Unchecked.defaultof<_>")
            |> ignore)

        sb.ToString()

    /// `let <>m0 (v0: T0) ... (vn: Tn) = expression`, one parameter per usable frame variable.
    let expressionSource (opens: string list) (variables: (string * string) list) (expression: string) =
        let sb = StringBuilder()
        let moduleName = ident queryModuleName
        let methodName = ident queryMethodName
        sb.AppendLine($"module {moduleName}") |> ignore
        appendOpens sb opens

        let parameters =
            match variables with
            | [] -> "()"
            | _ ->
                variables
                |> List.map (fun (name, text) ->
                    let parameterName = ident name
                    $"({parameterName}: {text})")
                |> String.concat " "

        sb.AppendLine($"let {methodName} {parameters} =") |> ignore

        for line in expression.Split('\n') do
            sb.Append("    ").AppendLine(line.TrimEnd('\r')) |> ignore

        sb.ToString()

    /// Lowers checked results to an IL module the way the hot reload in-process compile does,
    /// with minimal optimization so parameters survive as plain arguments.
    let compileToModule (results: FSharpCheckProjectResults) (outfile: string) =
        let tcConfig, tcGlobals, tcImports, unfinalizedCcu, ccuSig, topAttrsOpt, _, typedImplFilesOpt =
            results.CompilationData

        let ccuContents =
            Construct.NewCcuContents ILScopeRef.Local range0 unfinalizedCcu.AssemblyName ccuSig

        let generatedCcu = unfinalizedCcu.CloneWithFinalizedContents(ccuContents)

        let topAttrs =
            match topAttrsOpt with
            | Some attrs -> attrs
            | None -> invalidOp "The checked query has no assembly attributes."

        let typedImplFiles =
            match typedImplFilesOpt with
            | Some files -> files
            | None -> invalidOp "The checker was created without keepAssemblyContents."

        generatedCcu.Contents.SetAttribs(generatedCcu.Contents.Attribs @ topAttrs.assemblyAttrs)
        let exportRemapping = MakeExportRemapping generatedCcu generatedCcu.Contents

        let sigDataAttributes, sigDataResources =
            EncodeSignatureData(tcConfig, tcGlobals, exportRemapping, generatedCcu, outfile, false)

        let tcVal = LightweightTcValForUsingInBuildMethodCall tcGlobals
        let importMap = tcImports.GetImportMap()
        let optEnv0 = GetInitialOptimizationEnv(tcImports, tcGlobals)

        let settings =
            { tcConfig.optSettings with
                jitOptUser = Some false
                localOptUser = Some false
                crossAssemblyOptimizationUser = Some false
                lambdaInlineThreshold = 0
                abstractBigTargets = false
                reportingPhase = false
            }

        let optimizedImpls =
            typedImplFiles
            |> List.mapFold
                (fun (env, hidingInfo) implFile ->
                    let (env', file, _, hidingInfo'), optimizeDuringCodeGen =
                        Optimizer.OptimizeImplFile(
                            settings,
                            generatedCcu,
                            tcGlobals,
                            tcVal,
                            importMap,
                            env,
                            false,
                            tcConfig.emitTailcalls,
                            hidingInfo,
                            implFile
                        )

                    let file = LowerLocalMutables.TransformImplFile tcGlobals importMap file
                    let file = LowerCalls.LowerImplFile tcGlobals file

                    {
                        ImplFile = file
                        OptimizeDuringCodeGen = optimizeDuringCodeGen
                    },
                    (env', hidingInfo'))
                (optEnv0, SignatureHidingInfo.Empty)
            |> fst
            |> CheckedAssemblyAfterOptimization

        let ilxGenerator =
            CreateIlxAssemblyGenerator(tcConfig, tcImports, tcGlobals, tcVal, generatedCcu)

        let codegenResults =
            GenerateIlxCode(IlWriteBackend, false, tcConfig, topAttrs, optimizedImpls, generatedCcu.AssemblyName, ilxGenerator)

        let topAttrs =
            { topAttrs with
                assemblyAttrs = codegenResults.topAssemblyAttrs
            }

        let metadataVersion =
            match tcConfig.metadataVersion with
            | Some v -> v
            | None -> ""

        let ilxMainModule =
            MainModuleBuilder.CreateMainModule(
                CompilationThreadToken(),
                tcConfig,
                tcGlobals,
                tcImports,
                None,
                generatedCcu.AssemblyName,
                outfile,
                topAttrs,
                sigDataAttributes,
                sigDataResources,
                [],
                codegenResults,
                Some(ILVersionInfo(0us, 0us, 0us, 0us)),
                metadataVersion,
                mkILSecurityDecls codegenResults.permissionSets
            )

        { ilxMainModule with
            NativeResources = []
        },
        tcConfig,
        tcGlobals,
        tcImports

    let tryFindQueryMethod (modul: ILModuleDef) =
        modul.TypeDefs.AsList()
        |> List.tryFind (fun td -> td.Name = queryModuleName)
        |> Option.bind (fun td -> td.Methods.AsList() |> List.tryFind (fun m -> m.Name = queryMethodName))

    let replaceQueryMethod (modul: ILModuleDef) (replacement: ILMethodDef) =
        let typeDefs =
            modul.TypeDefs.AsList()
            |> List.map (fun td ->
                if td.Name = queryModuleName then
                    td.With(
                        methods =
                            mkILMethods (
                                td.Methods.AsList()
                                |> List.map (fun m -> if m.Name = queryMethodName then replacement else m)
                            )
                    )
                else
                    td)

        { modul with
            TypeDefs = mkILTypeDefs typeDefs
        }

    let writeModule
        (tcConfig: TcConfig)
        (tcGlobals: TcGlobals)
        (tcImports: TcImports)
        (accessTo: string list)
        (outfile: string)
        (modul: ILModuleDef)
        =
        let ctok = CompilationThreadToken()
        let modul = DebuggerFrame.withIgnoredAccessChecks tcGlobals.ilg accessTo modul

        let normalizeAssemblyRefs (aref: ILAssemblyRef) =
            match tcImports.TryFindDllInfo(ctok, rangeStartup, aref.Name, lookupOnly = false) with
            | Some dllInfo ->
                match dllInfo.ILScopeRef with
                | ILScopeRef.Assembly normalized -> normalized
                | _ -> aref
            | None -> aref

        let writerOptions: options =
            {
                ilg = tcGlobals.ilg
                outfile = outfile
                pdbfile = None
                portablePDB = false
                embeddedPDB = false
                embedAllSource = false
                embedSourceList = []
                allGivenSources = []
                sourceLink = ""
                checksumAlgorithm = tcConfig.checksumAlgorithm
                signer = None
                emitTailcalls = false
                deterministic = true
                dumpDebugInfo = false
                referenceAssemblyOnly = false
                referenceAssemblyAttribOpt = None
                referenceAssemblySignatureHash = None
                pathMap = tcConfig.pathMap
                moduleCustomDebugInfoRows = []
                methodCustomDebugInfoRows = Map.empty
            }

        let bytes, _pdb = WriteILBinaryInMemory(writerOptions, modul, normalizeAssemblyRefs)
        bytes

    let isBool (ty: ILType) =
        ty.IsNominal && ty.TypeRef.FullName = "System.Boolean"

[<Experimental("This FCS API is experimental and subject to change."); Sealed>]
type FSharpDebuggerExpressionCompiler(runtimeModulePath: string, referencePaths: seq<string>) =

    let referencePaths = List.ofSeq referencePaths
    let readers = Dictionary<string, ILModuleReader>(StringComparer.OrdinalIgnoreCase)

    let sessions =
        Dictionary<string, Result<CheckSession, string>>(StringComparer.OrdinalIgnoreCase)

    let contexts = Dictionary<string, FrameContext>(StringComparer.Ordinal)
    let mutable queryCount = 0

    let checker =
        lazy (FSharpChecker.Create(keepAssemblyContents = true, useTransparentCompiler = true))

    /// Loaded module paths by simple assembly name, first occurrence wins.
    let pathsByName =
        lazy
            (let map = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

             for path in referencePaths do
                 match Path.GetFileNameWithoutExtension path with
                 | null -> ()
                 | name ->
                     if not (map.ContainsKey name) then
                         map[name] <- path

             map)

    let moduleReader (path: string) =
        match readers.TryGetValue path with
        | true, reader -> reader
        | _ ->
            let reader = OpenILModuleReader path DebuggerFrame.readerOptions
            readers[path] <- reader
            reader

    let moduleOf (path: string) = (moduleReader path).ILModuleDef

    let runtimeModule = lazy (moduleOf runtimeModulePath)

    let targetProfile =
        if String.Equals(Path.GetFileNameWithoutExtension runtimeModulePath, "mscorlib", StringComparison.OrdinalIgnoreCase) then
            "mscorlib"
        else
            "netcore"

    let ilg =
        lazy
            (let primary =
                ILScopeRef.Assembly(mkRefToILAssembly runtimeModule.Value.ManifestOfAssembly)

             let fsharpCore =
                 match pathsByName.Value.TryGetValue "FSharp.Core" with
                 | true, path -> ILScopeRef.Assembly(mkRefToILAssembly (moduleOf path).ManifestOfAssembly)
                 | _ -> primary

             mkILGlobals (primary, [], fsharpCore))

    let nextAssemblyName () =
        queryCount <- queryCount + 1
        $"FSharpDebuggerQuery{queryCount}"

    /// The frame module, the core library, FSharp.Core and everything they reference transitively,
    /// resolved against the loaded modules by simple name. Unresolvable references are skipped.
    let referenceClosure (modulePath: string) =
        let byName = pathsByName.Value
        let visited = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let ordered = ResizeArray<string>()
        let pending = Queue<string>()

        let enqueue (path: string) =
            if visited.Add path then
                pending.Enqueue path

        enqueue modulePath
        enqueue runtimeModulePath

        for root in [ "FSharp.Core"; "System.Runtime"; "netstandard" ] do
            match byName.TryGetValue root with
            | true, path -> enqueue path
            | _ -> ()

        while pending.Count > 0 do
            let path = pending.Dequeue()
            ordered.Add path

            for aref in (moduleReader path).ILAssemblyRefs do
                match byName.TryGetValue aref.Name with
                | true, referenced -> enqueue referenced
                | _ -> ()

        List.ofSeq ordered

    let sessionFor (modulePath: string) =
        match sessions.TryGetValue modulePath with
        | true, session -> session
        | _ ->
            let references = referenceClosure modulePath

            let assemblyNames =
                references |> List.map (fun path -> (moduleOf path).ManifestOfAssembly.Name)

            let session =
                DebuggerCheck.warmUp checker.Value references assemblyNames targetProfile

            sessions[modulePath] <- session
            session

    let frameShape (frame: FSharpDebuggerFrame) =
        let modul = moduleOf frame.ModulePath

        match DebuggerFrame.tryFindMethod modul frame.MethodToken with
        | ValueNone -> Error $"Method 0x%08X{frame.MethodToken} was not found in '{frame.ModulePath}'."
        | ValueSome(struct (enclosing, tdef, mdef)) ->
            DebuggerFrame.frameShape (mkRefToILAssembly modul.ManifestOfAssembly) enclosing tdef mdef

    let localsInScope (frame: FSharpDebuggerFrame) =
        frame.LocalsInScope |> List.map (fun l -> l.Name, l.Slot)

    /// Resolves and caches what the checker can see of a frame: probes every candidate variable
    /// and `open` once, keeping only those the checker accepts.
    let frameContext (frame: FSharpDebuggerFrame) =
        let key =
            String.Join(
                "|",
                frame.ModulePath,
                string frame.MethodToken,
                String.Join(",", frame.LocalsInScope |> List.map (fun l -> $"{l.Name}:{l.Slot}"))
            )

        match contexts.TryGetValue key with
        | true, context -> Ok context
        | _ ->
            frameShape frame
            |> Result.bind (fun shape ->
                sessionFor frame.ModulePath
                |> Result.map (fun session ->
                    let typed =
                        DebuggerFrame.frameVariables shape (localsInScope frame)
                        |> List.choose (fun variable ->
                            match DebuggerCheck.typeText session variable.Type with
                            | ValueSome text -> Some(variable, text)
                            | ValueNone -> None)

                    let opens = DebuggerFrame.openPaths shape

                    let probe =
                        checker.Value.ParseAndCheckProject(
                            DebuggerCheck.snapshot
                                "<>probe"
                                session.References
                                session.TargetProfile
                                (DebuggerCheck.probeSource opens (typed |> List.map snd |> Array.ofList))
                        )
                        |> Async.RunSynchronously

                    let rejectedLines =
                        HashSet<int>(DebuggerCheck.errors probe |> Array.map (fun d -> d.StartLine))

                    let firstOpenLine = 2
                    let firstVariableLine = firstOpenLine + List.length opens

                    let keepAt firstLine items =
                        items
                        |> List.mapi (fun i item -> firstLine + i, item)
                        |> List.filter (fun (line, _) -> not (rejectedLines.Contains line))
                        |> List.map snd

                    let usable = keepAt firstVariableLine typed

                    let context =
                        {
                            Shape = shape
                            Variables = usable |> List.map fst |> Array.ofList
                            TypeTexts = usable |> List.map snd |> Array.ofList
                            Opens = keepAt firstOpenLine opens
                        }

                    contexts[key] <- context
                    context))

    member _.CompileLocalsQuery(frame: FSharpDebuggerFrame, argumentsOnly: bool) =
        frameShape frame
        |> Result.map (fun shape ->
            let variables =
                DebuggerFrame.frameVariables shape (localsInScope frame)
                |> List.filter (fun variable ->
                    match variable.Storage with
                    | VariableStorage.Argument _ -> true
                    | VariableStorage.LocalSlot _
                    | VariableStorage.ThisField _ -> not argumentsOnly)

            let accessors, entries =
                variables
                |> List.mapi (fun i variable ->
                    let methodName = $"<>m{i}"

                    DebuggerFrame.variableAccessor shape variable methodName,
                    {
                        Name = variable.Name
                        MethodName = methodName
                        IsArgument =
                            match variable.Storage with
                            | VariableStorage.Argument _ -> true
                            | _ -> false
                    })
                |> List.unzip

            let bytes =
                DebuggerFrame.writeQueryAssembly
                    ilg.Value
                    runtimeModule.Value.MetadataVersion
                    (nextAssemblyName ())
                    [ shape.AssemblyRef.Name ]
                    accessors

            {
                Assembly = bytes
                TypeName = DebuggerFrame.queryTypeName
                Locals = entries
            })

    member _.CompileExpression(frame: FSharpDebuggerFrame, expression: string) =
        frameContext frame
        |> Result.bind (fun context ->
            match sessions[frame.ModulePath] with
            | Error message -> Error message
            | Ok session ->
                let projectName = nextAssemblyName ()
                let outfile = projectName + ".dll"

                let variables =
                    Array.zip context.Variables context.TypeTexts
                    |> Array.map (fun (variable, text) -> variable.Name, text)
                    |> List.ofArray

                let results =
                    checker.Value.ParseAndCheckProject(
                        DebuggerCheck.snapshot
                            projectName
                            session.References
                            session.TargetProfile
                            (DebuggerCheck.expressionSource context.Opens variables expression)
                    )
                    |> Async.RunSynchronously

                match DebuggerCheck.errors results with
                | [||] ->
                    let modul, tcConfig, tcGlobals, tcImports =
                        DebuggerCheck.compileToModule results outfile

                    match DebuggerCheck.tryFindQueryMethod modul with
                    | None -> Error "The query method was not emitted."
                    | Some mdef when not (List.isEmpty mdef.GenericParams) ->
                        Error "The expression's type is not fully determined; add a type annotation."
                    | Some mdef ->
                        let rewritten =
                            DebuggerFrame.rewriteQueryMethod context.Shape context.Variables mdef

                        let modul = DebuggerCheck.replaceQueryMethod modul rewritten

                        Ok
                            {
                                Assembly = DebuggerCheck.writeModule tcConfig tcGlobals tcImports session.AssemblyNames outfile modul
                                TypeName = DebuggerCheck.queryModuleName
                                MethodName = DebuggerCheck.queryMethodName
                                ResultIsBool = DebuggerCheck.isBool mdef.Return.Type
                                HasSideEffects = false
                            }
                | diagnostics -> Error(DebuggerCheck.errorText diagnostics))

    interface IDisposable with
        member _.Dispose() =
            for reader in readers.Values do
                reader.Dispose()

            readers.Clear()
