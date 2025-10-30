namespace FSharp.Compiler.ComponentTests.HotReload

open Xunit
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.HotReloadBaseline
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter

module DeltaEmitterTests =

    let private createBaseline () =
        let ilg = PrimaryAssemblyILGlobals
        let methodBody =
            mkMethodBody (false, [], 2, nonBranchingInstrsToCode [ AI_ldc(DT_I4, ILConst.I4 1); I_ret ], None, None)

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

        let moduleDef =
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

        FSharp.Compiler.HotReloadBaseline.create moduleDef tokenMappings metadataSnapshot

    let private methodKey (baseline: FSharpEmitBaseline) name =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.Name = name)

    [<Fact>]
    let ``emitDelta projects known tokens`` () =
        let baseline = createBaseline ()
        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
            }

        let delta = emitDelta request

        Assert.Equal<int list>([ 0x02000001 ], delta.UpdatedTypeTokens)
        Assert.Equal<int list>([ 0x06000001 ], delta.UpdatedMethodTokens)
        Assert.Empty(delta.Metadata)
        Assert.Empty(delta.IL)
        Assert.True(delta.Pdb.IsNone)
        Assert.Empty(delta.EncLog)
        Assert.Empty(delta.EncMap)

    [<Fact>]
    let ``emitDelta ignores unknown symbols`` () =
        let baseline = createBaseline ()
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
            }

        let delta = emitDelta request

        Assert.Empty(delta.UpdatedTypeTokens)
        Assert.Empty(delta.UpdatedMethodTokens)
