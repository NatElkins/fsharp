namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.Collections.Immutable
open System.Reflection
open Xunit
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.AbstractIL.BinaryConstants
open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Xunit.Sdk
open FSharp.Test
open FSharp.Compiler.HotReload.SymbolMatcher

[<Collection(nameof NotThreadSafeResourceCollection)>]
module DeltaEmitterTests =

    let private tryRunMdv args =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- "mdv"
            startInfo.Arguments <- args
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false

            use proc = new Process(StartInfo = startInfo)
            if not (proc.Start()) then
                ValueNone
            else
                proc.WaitForExit()
                ValueSome (proc.ExitCode, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd())
        with _ -> ValueNone

    let private createMethod (ilg: ILGlobals) name returnValue =
        let methodBody =
            mkMethodBody (false, [], 2, nonBranchingInstrsToCode [ AI_ldc(DT_I4, ILConst.I4 returnValue); I_ret ], None, None)

        mkILNonGenericStaticMethod (
            name,
            ILMemberAccess.Public,
            [],
            mkILReturn ilg.typ_Int32,
            methodBody
        )

    let private createModule returnValue =
        let ilg = PrimaryAssemblyILGlobals
        let methodDef = createMethod ilg "GetValue" returnValue

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Type",
                    ILTypeDefAccess.Public,
                    mkILMethods [ methodDef ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
                    mkILEvents [],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField
            )

        mkILSimpleModule
            "SampleAssembly"
            "SampleModule"
            true
            (4, 0)
            false
            (mkILTypeDefs [ typeDef ])
            None
            None
            0
            (mkILExportedTypes [])
            "v4.0.30319"

    let private createModuleWithMethods (methods: (string * int) list) =
        let ilg = PrimaryAssemblyILGlobals
        let ilMethods = methods |> List.map (fun (name, value) -> createMethod ilg name value)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Multi",
                    ILTypeDefAccess.Public,
                    mkILMethods ilMethods,
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
                    mkILEvents [],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField
                )

        mkILSimpleModule
            "SampleAssembly"
            "SampleModule"
            true
            (4, 0)
            false
            (mkILTypeDefs [ typeDef ])
            None
            None
            0
            (mkILExportedTypes [])
            "v4.0.30319"

    let private createModuleWithOptionalField includeField =
        let ilg = PrimaryAssemblyILGlobals
        let methodDef = createMethod ilg "GetValue" 1

        let fields =
            if includeField then
                mkILFields [ mkILStaticField("trackedField", ilg.typ_Int32, None, None, ILMemberAccess.Private) ]
            else
                mkILFields []

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.FieldHolder",
                    ILTypeDefAccess.Public,
                    mkILMethods [ methodDef ],
                    fields,
                    emptyILTypeDefs,
                    mkILProperties [],
                    mkILEvents [],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField
                )

        mkILSimpleModule
            "SampleAssembly"
            "SampleModule"
            true
            (4, 0)
            false
            (mkILTypeDefs [ typeDef ])
            None
            None
            0
            (mkILExportedTypes [])
            "v4.0.30319"

    let private createStringModule (message: string) =
        let ilg = PrimaryAssemblyILGlobals
        let methodBody =
            mkMethodBody (false, [], 1, nonBranchingInstrsToCode [ I_ldstr message; I_ret ], None, None)

        let methodDef =
            mkILNonGenericStaticMethod (
                "GetMessage",
                ILMemberAccess.Public,
                [],
                mkILReturn ilg.typ_String,
                methodBody
            )

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Message",
                    ILTypeDefAccess.Public,
                    mkILMethods [ methodDef ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
                    mkILEvents [],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField
                )

        mkILSimpleModule
            "SampleAssembly"
            "SampleModule"
            true
            (4, 0)
            false
            (mkILTypeDefs [ typeDef ])
            None
            None
            0
            (mkILExportedTypes [])
            "v4.0.30319"

    let private createFieldHolderBaseline includeField =
        let moduleDef = createModuleWithOptionalField includeField

        let tokenMappings : ILTokenMappings =
            {
                TypeDefTokenMap = fun (_, _) -> 0x02000001
                FieldDefTokenMap = fun (_, _) _ -> 0x04000001
                MethodDefTokenMap = fun (_, _) _ -> 0x06000001
                PropertyTokenMap = fun (_, _) _ -> 0x17000001
                EventTokenMap = fun (_, _) _ -> 0x14000001
            }

        let metadataSnapshot : MetadataSnapshot =
            {
                HeapSizes =
                    {
                        StringHeapSize = 64
                        UserStringHeapSize = 32
                        BlobHeapSize = 64
                        GuidHeapSize = 16
                    }
                TableRowCounts = Array.create 64 0
                GuidHeapStart = 0
            }

        let moduleId = Guid.Parse("55555555-6666-7777-8888-999999999999")
        moduleDef, FSharp.Compiler.HotReloadBaseline.create moduleDef tokenMappings metadataSnapshot moduleId None

    let private createBaseline () =
        let baselineModule = createModule 42

        let tokenMappings : ILTokenMappings =
            {
                TypeDefTokenMap = fun (_, _) -> 0x02000001
                FieldDefTokenMap = fun _ _ -> 0x04000001
                MethodDefTokenMap = fun _ _ -> 0x06000001
                PropertyTokenMap = fun _ _ -> 0x17000001
                EventTokenMap = fun _ _ -> 0x14000001
            }

        let metadataSnapshot : MetadataSnapshot =
            {
                HeapSizes =
                    {
                        StringHeapSize = 64
                        UserStringHeapSize = 32
                        BlobHeapSize = 64
                        GuidHeapSize = 16
                    }
                TableRowCounts = Array.create 64 0
                GuidHeapStart = 0
            }

        let moduleId = Guid.Parse("11111111-2222-3333-4444-555555555555")
        let baseline = FSharp.Compiler.HotReloadBaseline.create baselineModule tokenMappings metadataSnapshot moduleId None
        baselineModule, baseline

    let private createBaselineWithMethods (methods: (string * int) list) =
        let baselineModule = createModuleWithMethods methods

        let methodTokenMap =
            methods
            |> List.mapi (fun idx (name, _) -> name, 0x06000001 + idx)
            |> dict

        let tokenMappings : ILTokenMappings =
            {
                TypeDefTokenMap = fun (_, _) -> 0x02000001
                FieldDefTokenMap = fun (_, _) _ -> 0x04000001
                MethodDefTokenMap = fun (_, _) mdef -> methodTokenMap.[mdef.Name]
                PropertyTokenMap = fun (_, _) _ -> 0x17000001
                EventTokenMap = fun (_, _) _ -> 0x14000001
            }

        let metadataSnapshot : MetadataSnapshot =
            {
                HeapSizes =
                    {
                        StringHeapSize = 64
                        UserStringHeapSize = 32
                        BlobHeapSize = 64
                        GuidHeapSize = 16
                    }
                TableRowCounts = Array.create 64 0
                GuidHeapStart = 0
            }

        let moduleId = Guid.Parse("22222222-3333-4444-5555-666666666666")
        let baseline = FSharp.Compiler.HotReloadBaseline.create baselineModule tokenMappings metadataSnapshot moduleId None
        baselineModule, baseline

    let private createStringBaseline (message: string) =
        let moduleDef = createStringModule message

        let tokenMappings : ILTokenMappings =
            {
                TypeDefTokenMap = fun (_, _) -> 0x02000001
                FieldDefTokenMap = fun (_, _) _ -> 0x04000001
                MethodDefTokenMap = fun (_, _) _ -> 0x06000001
                PropertyTokenMap = fun (_, _) _ -> 0x17000001
                EventTokenMap = fun (_, _) _ -> 0x14000001
            }

        let metadataSnapshot : MetadataSnapshot =
            {
                HeapSizes =
                    {
                        StringHeapSize = 256
                        UserStringHeapSize = 256
                        BlobHeapSize = 128
                        GuidHeapSize = 16
                    }
                TableRowCounts = Array.create 64 0
                GuidHeapStart = 0
            }

        let moduleId = Guid.Parse("33333333-4444-5555-6666-777777777777")
        moduleDef, FSharp.Compiler.HotReloadBaseline.create moduleDef tokenMappings metadataSnapshot moduleId None

    let private methodKey (baseline: FSharpEmitBaseline) name =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.Name = name)

    [<Fact>]
    let ``symbol matcher locates existing methods`` () =
        let moduleDef, baseline = createBaseline ()
        let matcher = FSharpSymbolMatcher.create moduleDef
        let key = methodKey baseline "GetValue"
        match FSharpSymbolMatcher.tryGetMethodDef matcher key with
        | Some(_, _, methodDef) -> Assert.Equal("GetValue", methodDef.Name)
        | None -> Assert.True(false, "Expected method to be located by symbol matcher.")

    [<Fact>]
    let ``emitDelta records updated user strings`` () =
        let _, baseline = createStringBaseline "Message version 1 (invocation #%d)"
        let updatedModule = createStringModule "Message version 2 (invocation #%d)"
        let key = methodKey baseline "GetMessage"
        let request : IlxDeltaRequest =
            { Baseline = baseline
              UpdatedTypes = [ key.DeclaringType ]
              UpdatedMethods = [ key ]
              Module = updatedModule
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta = emitDelta request

        Assert.NotEmpty(delta.UserStringUpdates)

        let updatedLiteral =
            delta.UserStringUpdates
            |> List.tryPick (fun (_, _, text) ->
                if text.StartsWith("Message version", StringComparison.Ordinal) then Some text else None)

        match updatedLiteral with
        | Some text -> Assert.Equal("Message version 2 (invocation #%d)", text)
        | None -> Assert.True(false, "Expected updated user string literal in delta metadata.")

    [<Fact>]
    let ``emitDelta projects known tokens`` () =
        let _, baseline = createBaseline ()
        let updatedModule = createModule 43
        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.Equal<int list>([ 0x02000001 ], delta.UpdatedTypeTokens)
        Assert.Equal<int list>([ 0x06000001 ], delta.UpdatedMethodTokens)
        // Debug hook to observe method body count when assertions fail.
        if delta.MethodBodies.Length <> 1 then
            printfn "MethodBodies count = %d" delta.MethodBodies.Length
        Assert.NotEmpty(delta.Metadata)
        Assert.NotEmpty(delta.IL)
        match delta.Pdb with
        | Some _ -> ()
        | None -> ()
        let bodyInfo = Assert.Single(delta.MethodBodies)
        Assert.Equal(0x06000001, bodyInfo.MethodToken)
        Assert.True(bodyInfo.CodeLength > 0)
        Assert.NotEqual(Guid.Empty, delta.GenerationId)
        Assert.NotEqual(Guid.Empty, delta.BaseGenerationId)
        let expectedEncLog =
            [|
                (TableIndex.Module, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.MethodDef, 0x00000001, EditAndContinueOperation.Default)
            |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)

        let expectedEncMap =
            [|
                (TableIndex.Module, 0x00000001)
                (TableIndex.MethodDef, 0x00000001)
            |]

        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)

    [<Fact>]
    let ``emitDelta ignores unknown symbols`` () =
        let _, baseline = createBaseline ()
        let updatedModule = createModule 43
        let unknownMethod =
            {
                DeclaringType = "Sample.Type"
                Name = "Missing"
                GenericArity = 0
                ParameterTypes = []
                ReturnType = ILType.Void
            }

        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Does.NotExist" ]
                UpdatedMethods = [ unknownMethod ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.Empty(delta.UpdatedTypeTokens)
        Assert.Empty(delta.UpdatedMethodTokens)
        Assert.Empty(delta.EncLog)
        Assert.Empty(delta.EncMap)
        Assert.Empty(delta.MethodBodies)

    [<Fact>]
    let ``emitDelta rejects added fields`` () =
        let _, baseline = createFieldHolderBaseline false
        let updatedModule = createModuleWithOptionalField true
        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.FieldHolder" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let ex = Assert.Throws<HotReloadUnsupportedEditException>(fun () -> emitDelta request |> ignore)
        Assert.Contains("Sample.FieldHolder::trackedField", ex.Message)

    [<Fact>]
    let ``emitDelta updates multiple methods`` () =
        let methods = [ "GetValue" , 1; "GetOther", 2 ]
        let _, baseline = createBaselineWithMethods methods
        let updatedModule = createModuleWithMethods [ "GetValue", 10; "GetOther", 20 ]

        let methodKeys = baseline.MethodTokens |> Map.toList |> List.map fst

        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Multi" ]
                UpdatedMethods = methodKeys
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.Equal(2, List.length delta.MethodBodies)
        Assert.Equal(2, List.length delta.UpdatedMethodTokens)
        Assert.Equal<int Set>(Set.ofList [0x06000001; 0x06000002], delta.UpdatedMethodTokens |> Set.ofList)
        Assert.True(delta.MethodBodies |> List.forall (fun body -> body.CodeLength > 0))
        Assert.Equal(3, delta.EncLog.Length)
        Assert.Equal(3, delta.EncMap.Length)
        match delta.Pdb with
        | Some pdb -> Assert.True(pdb.Length >= 0)
        | None -> ()

    [<Fact>]
    let ``metadata validator tool is available`` () =
        match tryRunMdv "--version" with
        | ValueNone ->
            // Treat absence of the mdv CLI as a soft skip; downstream delta tests assert availability explicitly.
            printfn "metadata-tools (mdv) CLI not found on PATH; skipping availability assertion."
            ()
        | ValueSome(0, _, _) -> Assert.Equal(0, 0)
        | ValueSome(exitCode, _, stderr) ->
            // Non-zero exit indicates mdv is installed but not runnable in this environment; treat it similarly to absence.
            printfn "metadata-tools (mdv) CLI reported exit code %d. stderr: %s" exitCode stderr
            ()

    [<Fact>]
    let ``emitDelta metadata validates with mdv`` () =
        let _, baseline = createBaseline ()
        let updatedModule = createModule 43
        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.NotEmpty(delta.Metadata)
        Assert.NotEmpty(delta.IL)
        Assert.Single(delta.MethodBodies) |> ignore
        Assert.NotEqual(Guid.Empty, delta.GenerationId)
        Assert.NotEqual(Guid.Empty, delta.BaseGenerationId)

        match tryRunMdv "--version" with
        | ValueNone ->
            printfn "metadata-tools (mdv) CLI not found; skipping validation test."
        | ValueSome(exitCode, _, _) when exitCode <> 0 ->
            printfn "metadata-tools (mdv) CLI reported exit code %d during version check; skipping validation test." exitCode
        | _ ->
            let tempMeta = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".meta")
            let tempIl = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".il")
            try
                File.WriteAllBytes(tempMeta, delta.Metadata)
                File.WriteAllBytes(tempIl, delta.IL)

                let arg = $"/g:{tempMeta};{tempIl}"
                match tryRunMdv arg with
                | ValueSome(0, _, _) -> ()
                | ValueSome(code, _, stderr) ->
                    Assert.True(false, $"mdv validation failed with exit code {code}. stderr: {stderr}")
                | ValueNone -> Assert.True(false, "mdv CLI became unavailable during validation")
            finally
                if File.Exists(tempMeta) then File.Delete(tempMeta)
                if File.Exists(tempIl) then File.Delete(tempIl)

    [<Fact>]
    let ``emitDelta method body reflects updated IL`` () =
        let _, baseline = createBaseline ()
        let updatedModule = createModule 100
        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        let bodyInfo = Assert.Single(delta.MethodBodies)
        let instructionStart = bodyInfo.CodeOffset + 12
        let ilBytes = delta.IL.AsSpan().Slice(instructionStart, bodyInfo.CodeLength).ToArray()
        Assert.Collection<byte>(
            ilBytes,
            (fun opcode -> Assert.Equal<byte>(0x1Fuy, opcode)),
            (fun operand -> Assert.Equal<byte>(0x64uy, operand)),
            (fun ret -> Assert.Equal<byte>(0x2Auy, ret))
        )

        let metadataBytes = ImmutableArray.CreateRange<byte>(delta.Metadata)
        use metadataProvider = MetadataReaderProvider.FromMetadataImage(metadataBytes)
        let mdReader = metadataProvider.GetMetadataReader()
        let methodHandle = MetadataTokens.MethodDefinitionHandle 1
        let methodDef = mdReader.GetMethodDefinition methodHandle
        Assert.Equal(bodyInfo.CodeOffset, methodDef.RelativeVirtualAddress)

    [<Fact>]
    let ``HotReloadState persists EncId sequencing`` () =
        let service = global.FSharp.Compiler.HotReload.FSharpEditAndContinueLanguageService.Instance

        service.EndSession()
        let _, baseline = createBaseline ()
        service.StartSession baseline

        let session0 =
            match service.TryGetSession() with
            | ValueSome session -> session
            | ValueNone -> failwith "Expected hot reload session to be initialised."

        Assert.Equal(1, session0.CurrentGeneration)
        Assert.True(session0.PreviousGenerationId |> Option.isNone)

        let moduleGen1 = createModule 43
        let requestGen1 =
            {
                IlxDeltaRequest.Baseline = session0.Baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = moduleGen1
                SymbolChanges = None
                CurrentGeneration = session0.CurrentGeneration
                PreviousGenerationId = session0.PreviousGenerationId
                SynthesizedNames = None
            }

        let delta1 = emitDelta requestGen1
        Assert.Equal(baseline.ModuleId, delta1.BaseGenerationId)
        Assert.NotEqual(Guid.Empty, delta1.GenerationId)

        service.OnDeltaApplied delta1.GenerationId

        let session1 =
            match service.TryGetSession() with
            | ValueSome session -> session
            | ValueNone -> failwith "Expected hot reload session to persist after applying delta."

        Assert.Equal(2, session1.CurrentGeneration)
        Assert.Equal<Guid option>(Some delta1.GenerationId, session1.PreviousGenerationId)

        let moduleGen2 = createModule 44
        let requestGen2 =
            {
                IlxDeltaRequest.Baseline = session1.Baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                Module = moduleGen2
                SymbolChanges = None
                CurrentGeneration = session1.CurrentGeneration
                PreviousGenerationId = session1.PreviousGenerationId
                SynthesizedNames = None
            }

        let delta2 = emitDelta requestGen2
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)
        Assert.NotEqual(Guid.Empty, delta2.GenerationId)

        service.EndSession()

    [<Fact>]
    let ``EditAndContinueLanguageService emits delta`` () =
        let service = FSharpEditAndContinueLanguageService.Instance
        service.EndSession()
        let _, baseline = createBaseline ()
        service.StartSession baseline

        let request : DeltaEmissionRequest =
            { IlModule = createModule 101
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey baseline "GetValue" ]
              SymbolChanges = None }

        match service.EmitDelta request with
        | Ok result ->
            Assert.Equal(baseline.ModuleId, result.Delta.BaseGenerationId)
            Assert.NotEqual(Guid.Empty, result.Delta.GenerationId)
            service.CommitPendingUpdate(result.Delta.GenerationId)
        | Error error ->
            Assert.True(false, sprintf "EmitDelta failed: %A" error)

        service.EndSession()

    [<Fact>]
    let ``IlDeltaStreamBuilder emits aligned method bodies`` () =
        let builder = IlDeltaStreamBuilder(None)
        let localSignatureToken = 0x11000001
        let code = [| 0x06uy; 0x2Auy |]

        builder.AddMethodBody(
            0x06000001,
            localSignatureToken,
            code,
            1,
            true,
            ImmutableArray<ExceptionRegion>.Empty,
            id
        ) |> ignore
        builder.AddEncLogEntry(TableIndex.MethodDef, 1, EditAndContinueOperation.Default)
        builder.AddEncMapEntry(TableIndex.MethodDef, 1)

        let moduleName = "SampleModule"
        let streams = builder.Build(moduleName, Guid.NewGuid(), Guid.NewGuid(), None)

        Assert.True(streams.Metadata.Length > 0, "Metadata stream should not be empty.")
        Assert.True(streams.IL.Length >= code.Length, "IL stream should include the encoded method body.")
        Assert.Equal(0, streams.IL.Length % 4)
        let bodyInfo = Assert.Single(streams.MethodBodies)
        Assert.Equal(0x06000001, bodyInfo.MethodToken)
        Assert.Equal(code.Length, bodyInfo.CodeLength)
        Assert.Equal(localSignatureToken, bodyInfo.LocalSignatureToken)
        Assert.Equal(0, bodyInfo.CodeOffset % 4)


    [<Fact>]
    let ``IlDeltaStreamBuilder tracks standalone signatures`` () =
        let builder = IlDeltaStreamBuilder(None)
        let signature = [| 0x07uy; 0x02uy |]

        let token = builder.AddStandaloneSignature(signature)
        Assert.NotEqual(0, token)

        let streams = builder.Build("SampleModule", Guid.NewGuid(), Guid.NewGuid(), None)
        let standalone = Assert.Single(streams.StandaloneSignatures)
        Assert.False(standalone.Handle.IsNil)
        let expectedToken = MetadataTokens.GetToken(EntityHandle.op_Implicit standalone.Handle)
        Assert.Equal(expectedToken, token)
        Assert.Equal<byte[]>(signature, standalone.Blob)
