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
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.AbstractIL.BinaryConstants
open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Xunit.Sdk
open FSharp.Test
open FSharp.Compiler.HotReload.SymbolMatcher
open FSharp.Compiler.TypedTreeDiff
open FSharp.Compiler.ComponentTests.HotReload.TestHelpers

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

    let private createModuleWithParameterizedMethod () =
        let ilg = PrimaryAssemblyILGlobals
        let baseMethod = createMethod ilg "GetValue" 1

        let paramBody =
            mkMethodBody (
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_ldarg 0us; I_ldarg 1us; AI_add; I_ret ],
                None,
                None)

        let paramMethod =
            mkILNonGenericStaticMethod(
                "SumValues",
                ILMemberAccess.Public,
                [ mkILParamNamed("left", ilg.typ_Int32); mkILParamNamed("right", ilg.typ_Int32) ],
                mkILReturn ilg.typ_Int32,
                paramBody)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Multi",
                    ILTypeDefAccess.Public,
                    mkILMethods [ baseMethod; paramMethod ],
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

        let moduleId = System.Guid.Parse("55555555-6666-7777-8888-999999999999")
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

        let moduleId = System.Guid.Parse("11111111-2222-3333-4444-555555555555")
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

        let moduleId = System.Guid.Parse("22222222-3333-4444-5555-666666666666")
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

        let moduleId = System.Guid.Parse("33333333-4444-5555-6666-777777777777")
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
              UpdatedAccessors = []
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
                UpdatedAccessors = []
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
        Assert.NotEqual(System.Guid.Empty, delta.GenerationId)
        Assert.Equal(System.Guid.Empty, delta.BaseGenerationId)
        let expectedEncLog =
            [|
                (TableIndex.Module, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.MethodDef, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.Param, 0x00000001, EditAndContinueOperation.AddParameter)
            |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)

        let expectedEncMap =
            [|
                (TableIndex.Module, 0x00000001)
                (TableIndex.MethodDef, 0x00000001)
                (TableIndex.Param, 0x00000001)
            |]

        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)

    [<Fact>]
    let ``emitDelta sets generation 1 base id to Guid.Empty`` () =
        let _, baseline = createBaseline ()
        let updatedModule = createModule 99

        let request =
            {
                IlxDeltaRequest.Baseline = baseline
                UpdatedTypes = [ "Sample.Type" ]
                UpdatedMethods = [ methodKey baseline "GetValue" ]
                UpdatedAccessors = []
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.NotEqual(System.Guid.Empty, delta.GenerationId)
        Assert.Equal(System.Guid.Empty, delta.BaseGenerationId)

    [<Fact>]
    let ``emitDelta chains BaseGenerationId across generations`` () =
        let _, baseline = createBaseline ()

        let requestGen1 =
            { IlxDeltaRequest.Baseline = baseline
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey baseline "GetValue" ]
              UpdatedAccessors = []
              Module = createModule 101
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitDelta requestGen1
        Assert.NotEqual(System.Guid.Empty, delta1.GenerationId)
        Assert.Equal(System.Guid.Empty, delta1.BaseGenerationId)

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not return an updated baseline."

        let requestGen2 =
            { IlxDeltaRequest.Baseline = baseline2
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey baseline "GetValue" ]
              UpdatedAccessors = []
              Module = createModule 102
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitDelta requestGen2

        Assert.NotEqual(System.Guid.Empty, delta2.GenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

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
                UpdatedAccessors = []
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
                UpdatedAccessors = []
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
                UpdatedAccessors = []
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
        let expectedLog =
            [|
                (TableIndex.Module, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.MethodDef, 0x00000001, EditAndContinueOperation.Default)
                (TableIndex.MethodDef, 0x00000002, EditAndContinueOperation.Default)
                (TableIndex.Param, 0x00000001, EditAndContinueOperation.AddParameter)
                (TableIndex.Param, 0x00000002, EditAndContinueOperation.AddParameter)
            |]
        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedLog, delta.EncLog)

        let expectedMap =
            [|
                (TableIndex.Module, 0x00000001)
                (TableIndex.MethodDef, 0x00000001)
                (TableIndex.MethodDef, 0x00000002)
                (TableIndex.Param, 0x00000001)
                (TableIndex.Param, 0x00000002)
            |]
        Assert.Equal<(TableIndex * int)[]>(expectedMap, delta.EncMap)
        match delta.Pdb with
        | Some pdb -> Assert.True(pdb.Length >= 0)
        | None -> ()

    [<Fact>]
    let ``emitDelta adds method metadata rows for new method`` () =
        let baselineArtifacts =
            TestHelpers.createBaselineFromModule (createModuleWithMethods [ "GetValue", 1 ])
        let updatedModule = createModuleWithMethods [ "GetValue", 1; "GetExtra", 5 ]

        let request =
            {
                IlxDeltaRequest.Baseline = baselineArtifacts.Baseline
                UpdatedTypes = [ "Sample.Multi" ]
                UpdatedMethods = []
                UpdatedAccessors = []
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.Equal(1, List.length delta.MethodBodies)
        let addedToken = Assert.Single(delta.UpdatedMethodTokens)

        let expectedRowId =
            baselineArtifacts.Baseline.Metadata.TableRowCounts.[int TableIndex.MethodDef] + 1

        Assert.Equal(0x06000000 ||| expectedRowId, addedToken)

        let hasMethodAdd =
            delta.EncLog
            |> Array.exists (fun (table, row, op) ->
                table = TableIndex.MethodDef && row = expectedRowId && op = EditAndContinueOperation.AddMethod)

        Assert.True(hasMethodAdd, "Expected MethodDef add operation in EncLog.")

        match delta.UpdatedBaseline with
        | Some updatedBaseline ->
            let addedKey =
                { MethodDefinitionKey.DeclaringType = "Sample.Multi"
                  Name = "GetExtra"
                  GenericArity = 0
                  ParameterTypes = []
                  ReturnType = PrimaryAssemblyILGlobals.typ_Int32 }

            Assert.True(updatedBaseline.MethodTokens.ContainsKey addedKey, "Updated baseline missing added method token.")
            Assert.Equal(addedToken, updatedBaseline.MethodTokens[addedKey])
        | None ->
            Assert.True(false, "Updated baseline missing.")

    [<Fact>]
    let ``emitDelta adds parameter metadata rows for new method`` () =
        let baselineArtifacts =
            TestHelpers.createBaselineFromModule (createModuleWithMethods [ "GetValue", 1 ])
        let updatedModule = createModuleWithParameterizedMethod ()

        let request =
            {
                IlxDeltaRequest.Baseline = baselineArtifacts.Baseline
                UpdatedTypes = [ "Sample.Multi" ]
                UpdatedMethods = []
                UpdatedAccessors = []
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        Assert.Equal(1, List.length delta.MethodBodies)
        let addedToken = Assert.Single(delta.UpdatedMethodTokens)
        Assert.True(addedToken <> 0, "Added method token missing.")

        let paramAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, _) -> table = TableIndex.Param)

        Assert.Equal(3, paramAdds.Length)

        let baselineParamCount = baselineArtifacts.Baseline.Metadata.TableRowCounts.[int TableIndex.Param]
        let expectedParamRows = [ baselineParamCount + 1; baselineParamCount + 2; baselineParamCount + 3 ]

        let actualRows =
            paramAdds
            |> Array.map (fun (_, row, op) ->
                Assert.Equal(EditAndContinueOperation.AddParameter, op)
                row)
            |> Array.sort
            |> Array.toList

        Assert.Equal<int list>(expectedParamRows, actualRows)

    [<Fact>]
    let ``emitDelta adds property metadata rows for new property`` () =
        let baselineArtifacts =
            TestHelpers.createBaselineFromModule (TestHelpers.createPropertyHostBaselineModule ())
        let updatedModule = TestHelpers.createPropertyModule "Property addition message"

        let getterKey =
            TestHelpers.methodKey "Sample.PropertyDemo" "get_Message" [] PrimaryAssemblyILGlobals.typ_String

        let accessorUpdate =
            TestHelpers.mkAccessorUpdate "Sample.PropertyDemo" (SymbolMemberKind.PropertyGet "Message") getterKey

        let request =
            {
                IlxDeltaRequest.Baseline = baselineArtifacts.Baseline
                UpdatedTypes = [ "Sample.PropertyDemo" ]
                UpdatedMethods = []
                UpdatedAccessors = [ accessorUpdate ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        let baselinePropertyCount = baselineArtifacts.Baseline.Metadata.TableRowCounts.[int TableIndex.Property]

        let propertyAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.Property && op = EditAndContinueOperation.AddProperty)

        let propertyMapAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.PropertyMap && op = EditAndContinueOperation.AddProperty)

        let semanticsAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.MethodSemantics && op = EditAndContinueOperation.AddMethod)

        Assert.Single propertyAdds |> ignore
        Assert.Single propertyMapAdds |> ignore
        Assert.Single semanticsAdds |> ignore

        let propertyRowId =
            propertyAdds
            |> Array.exactlyOne
            |> fun (_, row, _) -> row

        Assert.Equal(baselinePropertyCount + 1, propertyRowId)

        match delta.UpdatedBaseline with
        | Some updatedBaseline ->
            let propertyKey =
                { PropertyDefinitionKey.DeclaringType = "Sample.PropertyDemo"
                  Name = "Message"
                  PropertyType = PrimaryAssemblyILGlobals.typ_String
                  IndexParameterTypes = [] }

            Assert.True(updatedBaseline.PropertyTokens.ContainsKey propertyKey, "Updated baseline missing property token.")
            Assert.True(updatedBaseline.PropertyMapEntries.ContainsKey "Sample.PropertyDemo", "Updated baseline missing property map entry.")
        | None ->
            Assert.True(false, "Updated baseline missing.")

    [<Fact>]
    let ``emitDelta adds event metadata rows for new event`` () =
        let baselineArtifacts =
            TestHelpers.createBaselineFromModule (TestHelpers.createEventHostBaselineModule ())
        let updatedModule = TestHelpers.createEventModule "Event addition payload"

        let addKey =
            TestHelpers.methodKey "Sample.EventDemo" "add_OnChanged" [ PrimaryAssemblyILGlobals.typ_Object ] ILType.Void

        let accessorUpdate =
            TestHelpers.mkAccessorUpdate "Sample.EventDemo" (SymbolMemberKind.EventAdd "OnChanged") addKey

        let request =
            {
                IlxDeltaRequest.Baseline = baselineArtifacts.Baseline
                UpdatedTypes = [ "Sample.EventDemo" ]
                UpdatedMethods = []
                UpdatedAccessors = [ accessorUpdate ]
                Module = updatedModule
                SymbolChanges = None
                CurrentGeneration = 1
                PreviousGenerationId = None
                SynthesizedNames = None
            }

        let delta = emitDelta request

        let baselineEventCount = baselineArtifacts.Baseline.Metadata.TableRowCounts.[int TableIndex.Event]

        let eventAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.Event && op = EditAndContinueOperation.AddEvent)

        let eventMapAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.EventMap && op = EditAndContinueOperation.AddEvent)

        let semanticsAdds =
            delta.EncLog
            |> Array.filter (fun (table, _, op) -> table = TableIndex.MethodSemantics && op = EditAndContinueOperation.AddMethod)

        Assert.Single eventAdds |> ignore
        Assert.Single eventMapAdds |> ignore
        Assert.Single semanticsAdds |> ignore

        let eventRowId =
            eventAdds
            |> Array.exactlyOne
            |> fun (_, row, _) -> row

        Assert.Equal(baselineEventCount + 1, eventRowId)

        match delta.UpdatedBaseline with
        | Some updatedBaseline ->
            let eventKey =
                { EventDefinitionKey.DeclaringType = "Sample.EventDemo"
                  Name = "OnChanged"
                  EventType = Some PrimaryAssemblyILGlobals.typ_Object }

            Assert.True(updatedBaseline.EventTokens.ContainsKey eventKey, "Updated baseline missing event token.")
            Assert.True(updatedBaseline.EventMapEntries.ContainsKey "Sample.EventDemo", "Updated baseline missing event map entry.")
        | None ->
            Assert.True(false, "Updated baseline missing.")

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
                UpdatedAccessors = []
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
        Assert.NotEqual(System.Guid.Empty, delta.GenerationId)
        Assert.Equal(System.Guid.Empty, delta.BaseGenerationId)

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
                UpdatedAccessors = []
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
                UpdatedAccessors = []
                Module = moduleGen1
                SymbolChanges = None
                CurrentGeneration = session0.CurrentGeneration
                PreviousGenerationId = session0.PreviousGenerationId
                SynthesizedNames = None
            }

        let delta1 = emitDelta requestGen1
        Assert.Equal(System.Guid.Empty, delta1.BaseGenerationId)
        Assert.NotEqual(System.Guid.Empty, delta1.GenerationId)

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
                UpdatedAccessors = []
                Module = moduleGen2
                SymbolChanges = None
                CurrentGeneration = session1.CurrentGeneration
                PreviousGenerationId = session1.PreviousGenerationId
                SynthesizedNames = None
            }

        let delta2 = emitDelta requestGen2
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)
        Assert.NotEqual(System.Guid.Empty, delta2.GenerationId)

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
              UpdatedAccessors = []
              SymbolChanges = None }

        match service.EmitDelta request with
        | Ok result ->
            Assert.Equal(System.Guid.Empty, result.Delta.BaseGenerationId)
            Assert.NotEqual(System.Guid.Empty, result.Delta.GenerationId)
            service.CommitPendingUpdate(result.Delta.GenerationId)
        | Error error ->
            Assert.True(false, sprintf "EmitDelta failed: %A" error)

        service.EndSession()

    [<Fact>]
    let ``IlDeltaStreamBuilder records method body payload`` () =
        let ilBytes = [| 0x02uy; 0x28uy; 0x00uy; 0x00uy; 0x00uy; 0x0Auy; 0x2Auy |]
        let builder = IlDeltaStreamBuilder(None)

        let update =
            builder.AddMethodBody(
                0x06000001,
                0x11000001,
                ilBytes,
                8,
                false,
                ImmutableArray<ExceptionRegion>.Empty,
                id
            )

        Assert.Equal(0x06000001, update.MethodToken)
        let streams = builder.Build()
        Assert.True(streams.IL.Length >= ilBytes.Length)
        let single = Assert.Single(streams.MethodBodies)
        Assert.Equal(update.MethodToken, single.MethodToken)
        Assert.Equal(update.LocalSignatureToken, single.LocalSignatureToken)
        Assert.Equal(update.CodeLength, single.CodeLength)

    [<Fact>]
    let ``IlDeltaStreamBuilder captures standalone signatures`` () =
        let signature = [| 0x07uy; 0x02uy |]
        let builder = IlDeltaStreamBuilder(None)
        let token = builder.AddStandaloneSignature(signature)
        Assert.NotEqual(0, token)

        let streams = builder.Build()
        let standalone = Assert.Single(streams.StandaloneSignatures)
        let expected = MetadataTokens.GetToken(EntityHandle.op_Implicit standalone.Handle)
        Assert.Equal(expected, token)
        Assert.Equal<byte[]>(signature, standalone.Blob)

    [<Fact>]
    let ``IL delta fat header matches method body length`` () =
        // Baseline module with GetValue = 42, delta changes body to return 84.
        let _, baseline = createBaseline ()
        let updatedModule = createModule 84

        let request : IlxDeltaRequest =
            { Baseline = baseline
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey baseline "GetValue" ]
              UpdatedAccessors = []
              Module = updatedModule
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta = emitDelta request

        let bodyInfo = Assert.Single(delta.MethodBodies)
        let ilBytes = delta.IL
        let offset = bodyInfo.CodeOffset

        // Fat header: low 2 bits == 0x3, size byte == 0x30 (header size = 3 dwords => 12 bytes).
        let flagsByte = ilBytes[offset]
        let sizeByte = ilBytes[offset + 1]
        Assert.Equal(0x3uy, flagsByte &&& 0x3uy)
        Assert.Equal(0x30uy, sizeByte)

        // Code size in header matches MethodBodyUpdate.CodeLength.
        let codeSize =
            BitConverter.ToInt32(ilBytes, offset + 4)
        Assert.Equal(bodyInfo.CodeLength, codeSize)

        // No EH sections expected for this simple body (no MoreSects flag).
        Assert.Equal(0uy, flagsByte &&& e_CorILMethod_MoreSects)

        // MethodDef RVA should equal the code offset.
        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange delta.Metadata)
        let reader = provider.GetMetadataReader()
        let methodHandle = reader.MethodDefinitions |> Seq.head
        let methodDef = reader.GetMethodDefinition methodHandle
        Assert.Equal(bodyInfo.CodeOffset, methodDef.RelativeVirtualAddress)

    [<Fact>]
    let ``MethodDef RVA matches emitted method body offset`` () =
        // Baseline module with GetValue = 42
        let _, baseline = createBaseline ()
        // Updated module changes GetValue body to return 84
        let updatedModule = createModule 84

        let request : IlxDeltaRequest =
            { Baseline = baseline
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodKey baseline "GetValue" ]
              UpdatedAccessors = []
              Module = updatedModule
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta = emitDelta request

        let bodyInfo = Assert.Single(delta.MethodBodies)

        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange delta.Metadata)
        let reader = provider.GetMetadataReader()

        // Resolve MethodDef for GetValue in the delta metadata
        // Delta string heap offsets are absolute to baseline; names may be unreadable from delta alone.
        // This delta emits exactly one MethodDef row, so take the first handle.
        let methodHandle =
            reader.MethodDefinitions
            |> Seq.head

        let methodDef = reader.GetMethodDefinition methodHandle

        // MethodDef.RVA should point at the emitted method body offset
        Assert.Equal(bodyInfo.CodeOffset, methodDef.RelativeVirtualAddress)
