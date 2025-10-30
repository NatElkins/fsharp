namespace FSharp.Compiler.ComponentTests.HotReload

open Xunit
open FSharp.Compiler.IlxDeltaEmitter
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.HotReloadBaseline
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter

module DeltaEmitterTests =

    let private createBaseline () =
        let ilg = PrimaryAssemblyILGlobals
        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Type",
                    ILTypeDefAccess.Public,
                    mkILMethods [],
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

    [<Fact>]
    let ``emitDelta returns empty payload for placeholder implementation`` () =
        let baseline = createBaseline ()
        let request = { IlxDeltaRequest.Baseline = baseline }
        let delta = emitDelta request

        Assert.Empty(delta.Metadata)
        Assert.Empty(delta.IL)
        Assert.True(delta.Pdb.IsNone)
        Assert.Empty(delta.EncLog)
        Assert.Empty(delta.EncMap)
        Assert.Empty(delta.UpdatedTypeTokens)
        Assert.Empty(delta.UpdatedMethodTokens)
