namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open Xunit

open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers
open FSharp.Compiler.TypedTreeDiff
open FSharp.Test

[<Collection(nameof NotThreadSafeResourceCollection)>]
module PdbTests =

    let private createMethodWithSeqPoint (ilg: ILGlobals) name returnValue sourceFile =
        let document = ILSourceDocument.Create(None, None, None, sourceFile)
        let debugPoint = ILDebugPoint.Create(document, 1, 1, 1, 20)

        let methodBody =
            mkMethodBody (
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_seqpoint debugPoint; AI_ldc(DT_I4, ILConst.I4 returnValue); I_ret ],
                None,
                None
            )

        mkILNonGenericStaticMethod (name, ILMemberAccess.Public, [], mkILReturn ilg.typ_Int32, methodBody)

    let private createModuleWithSeqPoints returnValue =
        let ilg = PrimaryAssemblyILGlobals
        let methodDef = createMethodWithSeqPoint ilg "GetValue" returnValue "Sample.fs"

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

    let private createBaselineWithArtifacts returnValue =
        let moduleDef = createModuleWithSeqPoints returnValue

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

        let portablePdbSnapshot : PortablePdbSnapshot =
            {
                Bytes = Array.empty
                TableRowCounts = ImmutableArray.CreateRange(Array.create MetadataTokens.TableCount 0)
                EntryPointToken = None
            }

        let moduleId = Guid.Parse("99999999-0000-0000-0000-111111111111")

        let baseline =
            FSharp.Compiler.HotReloadBaseline.create
                moduleDef
                tokenMappings
                metadataSnapshot
                moduleId
                (Some portablePdbSnapshot)

        moduleDef, baseline

    let private baselineMethodKey (baseline: FSharpEmitBaseline) methodName =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.Name = methodName)

    [<Fact>]
    let ``emitDelta emits portable PDB delta with sequence points`` () =
        let _, baseline = createBaselineWithArtifacts 42
        let methodKey = baselineMethodKey baseline "GetValue"
        let methodToken = baseline.MethodTokens[methodKey]

        let updatedModule = createModuleWithSeqPoints 100

        let request : IlxDeltaRequest =
            { Baseline = baseline
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = updatedModule
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta = emitDelta request

        let pdbBytes =
            match delta.Pdb with
            | Some bytes -> bytes
            | None -> failwith "Expected portable PDB delta"

        use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
        let reader = provider.GetMetadataReader()

        let handles = reader.MethodDebugInformation |> Seq.toList
        let handle = Assert.Single(handles)
        let definitionHandle = handle.ToDefinitionHandle()
        let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
        let definitionToken = MetadataTokens.GetToken definitionEntity
        Assert.Equal(methodToken, definitionToken)

        let _methodInfo = reader.GetMethodDebugInformation handle
        ()

    [<Fact>]
    let ``emitDelta emits portable PDB delta for property accessor edits`` () =
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createPropertyModule "Property helper baseline message")
        let typeName = "Sample.PropertyDemo"
        let methodKey = TestHelpers.methodKeyByName artifacts.Baseline typeName "get_Message"
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]
        let accessorUpdate =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.PropertyGet "Message") methodKey

        let request : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ accessorUpdate ]
              Module = TestHelpers.createPropertyModule "Property helper updated message"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        try
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for property accessor edit."

            TestHelpers.assertBaselineDocument artifacts.PdbPath "PropertyDemo.fs"

            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange(pdbBytes))
            let reader = provider.GetMetadataReader()
            let matchingHandle =
                reader.MethodDebugInformation
                |> Seq.tryPick (fun handle ->
                    let definitionHandle = handle.ToDefinitionHandle()
                    let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
                    let definitionToken = MetadataTokens.GetToken definitionEntity
                    if definitionToken = methodToken then
                        Some(handle)
                    else
                        None)
            match matchingHandle with
            | None -> failwithf "Expected method token 0x%08X in portable PDB delta." methodToken
            | Some handle ->
                let definitionHandle = handle.ToDefinitionHandle()
                let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
                let definitionToken = MetadataTokens.GetToken definitionEntity
                Assert.Equal(methodToken, definitionToken)

                let info = reader.GetMethodDebugInformation handle
                Assert.False(info.Document.IsNil, "Expected property accessor to reference a source document.")

                let sequencePoints = info.GetSequencePoints() |> Seq.toArray
                Assert.NotEmpty(sequencePoints)
                let firstPoint = sequencePoints[0]
                Assert.Equal(1, firstPoint.StartLine)
                Assert.Equal(1, firstPoint.StartColumn)
        finally
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()

    [<Fact>]
    let ``emitDelta emits portable PDB delta for event accessor edits`` () =
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createEventModule "Event helper baseline payload")
        let typeName = "Sample.EventDemo"
        let methodKey = TestHelpers.methodKeyByName artifacts.Baseline typeName "add_OnChanged"
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]
        let accessorUpdate =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.EventAdd "OnChanged") methodKey

        let request : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ accessorUpdate ]
              Module = TestHelpers.createEventModule "Event helper updated payload"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        try
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for event accessor edit."

            TestHelpers.assertBaselineDocument artifacts.PdbPath "EventDemo.fs"

            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange(pdbBytes))
            let reader = provider.GetMetadataReader()
            let matchingHandle =
                reader.MethodDebugInformation
                |> Seq.tryPick (fun handle ->
                    let definitionHandle = handle.ToDefinitionHandle()
                    let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
                    let definitionToken = MetadataTokens.GetToken definitionEntity
                    if definitionToken = methodToken then
                        Some(handle)
                    else
                        None)
            match matchingHandle with
            | None -> failwithf "Expected method token 0x%08X in portable PDB delta." methodToken
            | Some handle ->
                let definitionHandle = handle.ToDefinitionHandle()
                let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
                let definitionToken = MetadataTokens.GetToken definitionEntity
                Assert.Equal(methodToken, definitionToken)

                let info = reader.GetMethodDebugInformation handle
                Assert.False(info.Document.IsNil, "Expected event accessor to reference a source document.")
                Assert.False(info.Document.IsNil, "Expected event accessor to reference a source document.")

                let sequencePoints = info.GetSequencePoints() |> Seq.toArray
                Assert.NotEmpty(sequencePoints)
                let firstPoint = sequencePoints[0]
                Assert.Equal(1, firstPoint.StartLine)
        finally
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()

    [<Fact>]
    let ``emitDelta emits portable PDB delta for added property accessor`` () =
        let baselineModule = TestHelpers.createPropertyHostBaselineModule ()
        let artifacts = TestHelpers.createBaselineFromModule baselineModule
        let typeName = "Sample.PropertyDemo"
        let accessorKey = TestHelpers.methodKey typeName "get_Message" [] PrimaryAssemblyILGlobals.typ_String
        let accessorUpdate =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.PropertyGet "Message") accessorKey

        let request : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = []
              UpdatedAccessors = [ accessorUpdate ]
              Module = TestHelpers.createPropertyModule "Property helper added message"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        try
            let delta = emitDelta request

            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for added property accessor."

            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
            let reader = provider.GetMetadataReader()
            let info =
                reader.MethodDebugInformation
                |> Seq.map reader.GetMethodDebugInformation
                |> Seq.head

            Assert.False(info.Document.IsNil, "Expected added property accessor to carry document info.")
            let points = info.GetSequencePoints() |> Seq.toArray
            Assert.NotEmpty(points)
        finally
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()

    [<Fact>]
    let ``emitDelta emits portable PDB delta for added event accessor`` () =
        let baselineModule = TestHelpers.createEventHostBaselineModule ()
        let artifacts = TestHelpers.createBaselineFromModule baselineModule
        let typeName = "Sample.EventDemo"
        let accessorKey = TestHelpers.methodKey typeName "add_OnChanged" [ PrimaryAssemblyILGlobals.typ_Object ] ILType.Void
        let accessorUpdate =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.EventAdd "OnChanged") accessorKey

        let request : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = []
              UpdatedAccessors = [ accessorUpdate ]
              Module = TestHelpers.createEventModule "Event helper added payload"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        try
            let delta = emitDelta request

            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for added event accessor."

            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange(pdbBytes))
            let reader = provider.GetMetadataReader()
            let infos =
                reader.MethodDebugInformation
                |> Seq.map reader.GetMethodDebugInformation
                |> Seq.toArray

            Assert.NotEmpty infos
            infos
            |> Array.iter (fun info ->
                Assert.False(info.Document.IsNil, "Expected added event accessors to carry document info.")
                let points = info.GetSequencePoints() |> Seq.toArray
                Assert.NotEmpty(points))
        finally
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()
