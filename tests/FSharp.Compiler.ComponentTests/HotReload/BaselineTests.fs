namespace FSharp.Compiler.ComponentTests.HotReload

open System.Collections.Generic
open System.Reflection

open Xunit

open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.HotReloadBaseline
open Internal.Utilities

module BaselineTests =

    let private mkSimpleMethodBody instrs =
        let code = nonBranchingInstrsToCode instrs
        mkMethodBody (false, [], 8, code, None, None)

    let private createSampleModule () =
        let ilg = PrimaryAssemblyILGlobals
        let intType = ilg.typ_Int32
        let objectType = ilg.typ_Object

        let staticMethod =
            mkILNonGenericStaticMethod (
                "GetValue",
                ILMemberAccess.Public,
                [ mkILParamNamed ("input", intType) ],
                mkILReturn intType,
                mkSimpleMethodBody
                    [ AI_ldc(DT_I4, ILConst.I4 1)
                      I_ret ]
            )

        let baseGetter =
            mkILNonGenericInstanceMethod (
                "get_Data",
                ILMemberAccess.Public,
                [],
                mkILReturn intType,
                mkSimpleMethodBody
                    [ AI_ldc(DT_I4, ILConst.I4 2)
                      I_ret ]
            )

        let getter =
            baseGetter.With(attributes = (baseGetter.Attributes ||| MethodAttributes.SpecialName ||| MethodAttributes.HideBySig))

        let baseSetter =
            mkILNonGenericInstanceMethod (
                "set_Data",
                ILMemberAccess.Public,
                [ mkILParamNamed ("value", intType) ],
                mkILReturn ILType.Void,
                mkSimpleMethodBody
                    [ I_ret ]
            )

        let setter =
            baseSetter.With(attributes = (baseSetter.Attributes ||| MethodAttributes.SpecialName ||| MethodAttributes.HideBySig))

        let baseAdd =
            mkILNonGenericInstanceMethod (
                "add_OnChanged",
                ILMemberAccess.Public,
                [ mkILParamNamed ("handler", objectType) ],
                mkILReturn ILType.Void,
                mkSimpleMethodBody
                    [ I_ret ]
            )

        let addHandler =
            baseAdd.With(attributes = (baseAdd.Attributes ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName))

        let baseRemove =
            mkILNonGenericInstanceMethod (
                "remove_OnChanged",
                ILMemberAccess.Public,
                [ mkILParamNamed ("handler", objectType) ],
                mkILReturn ILType.Void,
                mkSimpleMethodBody
                    [ I_ret ]
            )

        let removeHandler =
            baseRemove.With(attributes = (baseRemove.Attributes ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName))

        let fieldDef = mkILInstanceField ("valueBackingField", intType, None, ILMemberAccess.Private)

        let typeRef = mkILTyRef (ILScopeRef.Local, "Sample.Container")

        let propertyDef =
            ILPropertyDef(
                "Data",
                PropertyAttributes.None,
                Some(mkILMethRef (typeRef, ILCallingConv.Instance, "set_Data", 0, [ intType ], ILType.Void)),
                Some(mkILMethRef (typeRef, ILCallingConv.Instance, "get_Data", 0, [], intType)),
                ILThisConvention.Instance,
                intType,
                None,
                [],
                emptyILCustomAttrs
            )

        let eventDef =
            ILEventDef(
                Some objectType,
                "OnChanged",
                EventAttributes.None,
                mkILMethRef (typeRef, ILCallingConv.Instance, "add_OnChanged", 0, [ objectType ], ILType.Void),
                mkILMethRef (typeRef, ILCallingConv.Instance, "remove_OnChanged", 0, [ objectType ], ILType.Void),
                None,
                [],
                emptyILCustomAttrs
            )

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Container",
                    ILTypeDefAccess.Public,
                    mkILMethods [ staticMethod; getter; setter; addHandler; removeHandler ],
                    mkILFields [ fieldDef ],
                    emptyILTypeDefs,
                    mkILProperties [ propertyDef ],
                    mkILEvents [ eventDef ],
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

    let private emitBaseline () =
        let ilModule = createSampleModule ()
        let ilg = PrimaryAssemblyILGlobals

        let options: options =
            { ilg = ilg
              outfile = "Sample.dll"
              pdbfile = None
              portablePDB = false
              embeddedPDB = false
              embedAllSource = false
              embedSourceList = []
              allGivenSources = []
              sourceLink = ""
              checksumAlgorithm = HashAlgorithm.Sha256
              signer = None
              emitTailcalls = false
              deterministic = true
              dumpDebugInfo = false
              referenceAssemblyOnly = false
              referenceAssemblyAttribOpt = None
              referenceAssemblySignatureHash = None
              pathMap = PathMap.empty }

        let _, _, tokenMappings, metadataSnapshot =
            WriteILBinaryInMemoryWithArtifacts (options, ilModule, id)

        create ilModule tokenMappings metadataSnapshot

    [<Fact>]
    let ``baseline tokens are stable across emissions`` () =
        let first = emitBaseline ()
        let second = emitBaseline ()

        Assert.Equal<Map<string, int>>(first.TypeTokens, second.TypeTokens)
        Assert.Equal<Map<MethodDefinitionKey, int>>(first.MethodTokens, second.MethodTokens)
        Assert.Equal<Map<FieldDefinitionKey, int>>(first.FieldTokens, second.FieldTokens)
        Assert.Equal<Map<PropertyDefinitionKey, int>>(first.PropertyTokens, second.PropertyTokens)
        Assert.Equal<Map<EventDefinitionKey, int>>(first.EventTokens, second.EventTokens)

    [<Fact>]
    let ``baseline captures expected members`` () =
        let baseline = emitBaseline ()
        let ilg = PrimaryAssemblyILGlobals

        Assert.True(Map.containsKey "Sample.Container" baseline.TypeTokens)

        let methodKey =
            { DeclaringType = "Sample.Container"
              Name = "GetValue"
              GenericArity = 0
              ParameterTypes = [ ilg.typ_Int32 ]
              ReturnType = ilg.typ_Int32 }

        Assert.True(Map.containsKey methodKey baseline.MethodTokens)

        let fieldKey =
            { DeclaringType = "Sample.Container"
              Name = "valueBackingField"
              FieldType = ilg.typ_Int32 }

        Assert.True(Map.containsKey fieldKey baseline.FieldTokens)

        let propertyKey =
            { DeclaringType = "Sample.Container"
              Name = "Data"
              PropertyType = ilg.typ_Int32
              IndexParameterTypes = [] }

        Assert.True(Map.containsKey propertyKey baseline.PropertyTokens)

        let eventKey =
            { DeclaringType = "Sample.Container"
              Name = "OnChanged"
              EventType = Some ilg.typ_Object }

        Assert.True(Map.containsKey eventKey baseline.EventTokens)

    [<Fact>]
    let ``metadata snapshot captures heap lengths`` () =
        let baseline = emitBaseline ()
        let heaps = baseline.Metadata.HeapSizes

        Assert.True(heaps.StringHeapSize > 0)
        Assert.True(heaps.BlobHeapSize >= 0)
        Assert.Equal(64, baseline.Metadata.TableRowCounts.Length)
        Assert.True(baseline.Metadata.GuidHeapStart >= 0)
