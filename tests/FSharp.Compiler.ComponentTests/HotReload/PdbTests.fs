namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.Collections.Immutable
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
