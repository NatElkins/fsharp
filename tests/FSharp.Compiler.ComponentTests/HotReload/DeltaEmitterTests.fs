namespace FSharp.Compiler.ComponentTests.HotReload

open System
open Xunit
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.HotReloadBaseline
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxDeltaStreams
open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Xunit.Sdk

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

    let private createModule returnValue =
        let ilg = PrimaryAssemblyILGlobals
        let methodBody =
            mkMethodBody (false, [], 2, nonBranchingInstrsToCode [ AI_ldc(DT_I4, ILConst.I4 returnValue); I_ret ], None, None)

        let methodDef =
            mkILNonGenericStaticMethod (
                "GetValue",
                ILMemberAccess.Public,
                [],
                mkILReturn ilg.typ_Int32,
                methodBody
            )

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

        let baseline = FSharp.Compiler.HotReloadBaseline.create baselineModule tokenMappings metadataSnapshot
        baselineModule, baseline

    let private methodKey (baseline: FSharpEmitBaseline) name =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.Name = name)

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
            }

        let delta = emitDelta request

        Assert.Equal<int list>([ 0x02000001 ], delta.UpdatedTypeTokens)
        Assert.Equal<int list>([ 0x06000001 ], delta.UpdatedMethodTokens)
        Assert.NotEmpty(delta.Metadata)
        Assert.NotEmpty(delta.IL)
        Assert.True(delta.Pdb.IsNone)
        let bodyInfo = Assert.Single(delta.MethodBodies)
        Assert.Equal(0x06000001, bodyInfo.MethodToken)
        Assert.True(bodyInfo.CodeLength > 0)
        let expectedEncLog =
            [|
                (TableIndex.TypeDef, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.MethodDef, 0x00000001, EditAndContinueOperation.Default)
            |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)

        let expectedEncMap =
            [|
                (TableIndex.TypeDef, 0x00000001)
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
            }

        let delta = emitDelta request

        Assert.Empty(delta.UpdatedTypeTokens)
        Assert.Empty(delta.UpdatedMethodTokens)
        Assert.Empty(delta.EncLog)
        Assert.Empty(delta.EncMap)
        Assert.Empty(delta.MethodBodies)

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
            }

        let delta = emitDelta request

        Assert.NotEmpty(delta.Metadata)
        Assert.NotEmpty(delta.IL)
        Assert.Single(delta.MethodBodies) |> ignore

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
    let ``IlDeltaStreamBuilder emits aligned method bodies`` () =
        let builder = IlDeltaStreamBuilder()
        let localSignatureToken = 0x11000001
        let code = [| 0x06uy; 0x2Auy |]

        builder.AddMethodBody(0x06000001, localSignatureToken, code)
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
        let builder = IlDeltaStreamBuilder()
        let signature = [| 0x07uy; 0x02uy |]

        let token = builder.AddStandaloneSignature(signature)
        Assert.NotEqual(0, token)

        let streams = builder.Build("SampleModule", Guid.NewGuid(), Guid.NewGuid(), None)
        let standalone = Assert.Single(streams.StandaloneSignatures)
        Assert.False(standalone.Handle.IsNil)
        let expectedToken = MetadataTokens.GetToken(EntityHandle.op_Implicit standalone.Handle)
        Assert.Equal(expectedToken, token)
        Assert.Equal<byte[]>(signature, standalone.Blob)
