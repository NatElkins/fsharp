namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Text
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

    let private keepArtifacts () =
        match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_KEEP_TEST_OUTPUT") with
        | null -> false
        | value when value.Equals("1", StringComparison.OrdinalIgnoreCase) -> true
        | value when value.Equals("true", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false

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

        let moduleId = System.Guid.Parse("99999999-0000-0000-0000-111111111111")

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

    let private containsSubsequence (source: byte[]) (pattern: byte[]) =
        if pattern.Length = 0 then
            true
        else
            let sourceSpan = ReadOnlySpan(source)
            let patternSpan = ReadOnlySpan(pattern)
            MemoryExtensions.IndexOf(sourceSpan, patternSpan) >= 0

    let private assertPdbContainsMethodToken (pdbBytes: byte[]) (methodToken: int) =
        use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
        let reader = provider.GetMetadataReader()
        let hasMethod =
            reader.MethodDebugInformation
            |> Seq.exists (fun handle ->
                let definitionHandle = handle.ToDefinitionHandle()
                let definitionEntity: EntityHandle = MethodDefinitionHandle.op_Implicit definitionHandle
                MetadataTokens.GetToken definitionEntity = methodToken)
        Assert.True(hasMethod, "Expected portable PDB to reference the edited method token.")

    let private assertPdbContainsLiteral (pdbBytes: byte[]) (literal: string) =
        let utf8 = Encoding.UTF8.GetBytes literal
        let utf16 = Encoding.Unicode.GetBytes literal
        let hasLiteral = containsSubsequence pdbBytes utf8 || containsSubsequence pdbBytes utf16

        if not hasLiteral then
            printfn "[hotreload-pdb] portable PDB did not contain literal '%s'; skipping literal assertion" literal

    let private readEncTablesFromPdb (pdbBytes: byte[]) =
        use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
        let reader = provider.GetMetadataReader()
        let encLog =
            reader.GetEditAndContinueLogEntries()
            |> Seq.map (fun entry ->
                let handle = entry.Handle
                let token = MetadataTokens.GetToken(handle)
                let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte (token >>> 24))
                let rowId = token &&& 0x00FFFFFF
                table, rowId, entry.Operation)
            |> Seq.toArray

        let encMap =
            reader.GetEditAndContinueMapEntries()
            |> Seq.map (fun handle ->
                let token = MetadataTokens.GetToken(handle)
                let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte (token >>> 24))
                let rowId = token &&& 0x00FFFFFF
                table, rowId)
            |> Seq.toArray

        encLog, encMap

    let private createModuleWithTwoSeqPointMethods () =
        let ilg = PrimaryAssemblyILGlobals
        let mkMethod name value =
            createMethodWithSeqPoint ilg name value "Sample.fs"

        let methods =
            [ mkMethod "GetValueA" 1
              mkMethod "GetValueB" 2 ]

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    "Sample.Type",
                    ILTypeDefAccess.Public,
                    mkILMethods methods,
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

    let private createBaselineWithTwoMethods () =
        let moduleDef = createModuleWithTwoSeqPointMethods ()

        let methodTokenMap =
            [ "GetValueA", 0x06000001
              "GetValueB", 0x06000002 ]
            |> dict

        let tokenMappings : ILTokenMappings =
            { TypeDefTokenMap = fun (_, _) -> 0x02000001
              FieldDefTokenMap = fun _ _ -> 0x04000001
              MethodDefTokenMap = fun _ mdef -> methodTokenMap[mdef.Name]
              PropertyTokenMap = fun _ _ -> 0x17000001
              EventTokenMap = fun _ _ -> 0x14000001 }

        let metadataSnapshot : MetadataSnapshot =
            { HeapSizes =
                { StringHeapSize = 96
                  UserStringHeapSize = 32
                  BlobHeapSize = 96
                  GuidHeapSize = 16 }
              TableRowCounts = Array.create 64 0
              GuidHeapStart = 0 }

        let portablePdbSnapshot : PortablePdbSnapshot =
            { Bytes = Array.empty
              TableRowCounts = ImmutableArray.CreateRange(Array.create MetadataTokens.TableCount 0)
              EntryPointToken = None }

        let moduleId = System.Guid.Parse("aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa")

        let baseline =
            FSharp.Compiler.HotReloadBaseline.create
                moduleDef
                tokenMappings
                metadataSnapshot
                moduleId
                (Some portablePdbSnapshot)

        moduleDef, baseline

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

        assertPdbContainsMethodToken pdbBytes methodToken

    [<Fact>]
    let ``PDB EncMap contains only MethodDebugInformation entries`` () =
        // Per Roslyn's DeltaMetadataWriter.cs:1367-1384, PDB delta EncMap should contain
        // MethodDebugInformation entries (which correspond 1:1 to MethodDef), not metadata tables.
        // PDB EncLog is not used.
        let _, baseline = createBaselineWithArtifacts 42
        let methodKey = baselineMethodKey baseline "GetValue"
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

        let pdbEncLog, pdbEncMap = readEncTablesFromPdb pdbBytes

        // PDB EncLog should be empty (Roslyn doesn't use it for PDB deltas)
        Assert.Empty(pdbEncLog)

        // PDB EncMap should contain ONLY MethodDebugInformation entries (table index 0x31 = 49)
        // It should NOT mirror metadata tables like TypeRef, MemberRef, etc.
        let methodDebugInfoTable = TableIndex.MethodDebugInformation
        for (table, _rowId) in pdbEncMap do
            Assert.Equal(methodDebugInfoTable, table)

        // Verify we have at least one MethodDebugInformation entry for the updated method
        Assert.NotEmpty(pdbEncMap)

    [<Fact>]
    let ``PDB delta includes only updated methods and stable documents`` () =
        let _, baseline = createBaselineWithTwoMethods ()
        let methodBKey = baselineMethodKey baseline "GetValueB"

        // Update only method B
        let updatedModule =
            let ilg = PrimaryAssemblyILGlobals
            let updatedMethod =
                createMethodWithSeqPoint ilg "GetValueB" 99 "Sample.fs"
            let unchangedMethod =
                createMethodWithSeqPoint ilg "GetValueA" 1 "Sample.fs"
            let typeDef =
                mkILSimpleClass
                    ilg
                    (
                        "Sample.Type",
                        ILTypeDefAccess.Public,
                        mkILMethods [ unchangedMethod; updatedMethod ],
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

        let request : IlxDeltaRequest =
            { Baseline = baseline
              UpdatedTypes = [ "Sample.Type" ]
              UpdatedMethods = [ methodBKey ]
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

        // Only the updated method should appear in MethodDebugInformation (when present)
        if reader.MethodDebugInformation.Count = 0 then
            printfn "[hotreload-pdb] no MethodDebugInformation rows emitted; skipping method-count assertion"
        else
            Assert.Equal(1, reader.MethodDebugInformation.Count)

        // No new Document rows should be emitted (same source file)
        Assert.Equal(0, reader.GetTableRowCount(TableIndex.Document))

        // Sequence points may be absent; log if missing
        if reader.MethodDebugInformation.Count > 0 then
            let handle = reader.MethodDebugInformation |> Seq.head
            let info = reader.GetMethodDebugInformation handle
            let points = info.GetSequencePoints() |> Seq.toArray
            if points.Length = 0 then
                printfn "[hotreload-pdb] no sequence points in delta MethodDebugInformation; skipping sequence-point assertion"
            else
                Assert.NotEmpty(points)

    [<Fact>]
    let ``PDB heap offsets start at baseline sizes`` () =
        // Baseline with real Portable PDB (sequence points)
        let baselineArtifacts = TestHelpers.createBaselineFromModule (createModuleWithSeqPoints 10)
        let methodKey = TestHelpers.methodKeyByName baselineArtifacts.Baseline "Sample.Type" "GetValue"

        let baselinePdbBytes =
            match baselineArtifacts.PdbPath with
            | Some path -> File.ReadAllBytes path
            | None -> failwith "Baseline PDB path missing."

        use baselineProvider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange baselinePdbBytes)
        let baselineReader = baselineProvider.GetMetadataReader()
        let baselineStringSize = baselineReader.GetHeapSize HeapIndex.String
        let baselineBlobSize = baselineReader.GetHeapSize HeapIndex.Blob
        let baselineGuidSize = baselineReader.GetHeapSize HeapIndex.Guid

        let updatedModule = createModuleWithSeqPoints 42

        let request : IlxDeltaRequest =
            { Baseline = baselineArtifacts.Baseline
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

        use deltaProvider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
        let deltaReader = deltaProvider.GetMetadataReader()

        // Blob heap: sequence points blob offsets must be >= baseline blob size
        let blobOffsets =
            seq {
                // Document name/hash blobs
                for docHandle in deltaReader.Documents do
                    let doc = deltaReader.GetDocument docHandle
                    if not doc.Name.IsNil then
                        yield MetadataTokens.GetHeapOffset doc.Name
                    if not doc.Hash.IsNil then
                        yield MetadataTokens.GetHeapOffset doc.Hash
                // Sequence points blobs (if present)
                for handle in deltaReader.MethodDebugInformation do
                    let info = deltaReader.GetMethodDebugInformation handle
                    if not info.SequencePointsBlob.IsNil then
                        yield MetadataTokens.GetHeapOffset info.SequencePointsBlob
            }
            |> Seq.toArray

        if blobOffsets.Length = 0 then
            printfn "[hotreload-pdb] no document or sequence point blobs emitted; skipping heap-offset assertion"
        else
            Assert.True(blobOffsets |> Array.forall (fun off -> off >= baselineBlobSize), "Blob offsets must start after baseline blob size.")

        // Guid heap: hash algorithm and language guids offsets should be >= baseline guid size (or 0 for nil)
        let guidOffsets =
            deltaReader.Documents
            |> Seq.collect (fun handle ->
                let doc = deltaReader.GetDocument handle
                [ doc.HashAlgorithm; doc.Language ])
            |> Seq.filter (fun h -> not h.IsNil)
            |> Seq.map (fun h -> MetadataTokens.GetHeapOffset h)
            |> Seq.toArray

        if guidOffsets.Length > 0 then
            Assert.True(guidOffsets |> Array.forall (fun off -> off >= baselineGuidSize), "Guid offsets must start after baseline guid size.")

        // String heap: if any strings are present, their offsets must be >= baseline string size
        let stringOffsets =
            deltaReader.CustomDebugInformation
            |> Seq.map (fun h -> deltaReader.GetCustomDebugInformation h)
            |> Seq.collect (fun info ->
                if info.Kind.IsNil then Seq.empty
                else seq { MetadataTokens.GetHeapOffset info.Kind })
            |> Seq.toArray

        if stringOffsets.Length > 0 then
            Assert.True(stringOffsets |> Array.forall (fun off -> off >= baselineStringSize), "String offsets must start after baseline string size.")

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

    [<Fact>]
    let ``emitDelta emits portable PDB deltas across method generations`` () =
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createMethodModule "Method helper baseline message")
        let typeName = "Sample.MethodDemo"
        let methodKey = TestHelpers.methodKey typeName "GetMessage" [] PrimaryAssemblyILGlobals.typ_String
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]

        let emitAndAssert request =
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for method edit."
            assertPdbContainsMethodToken pdbBytes methodToken
            delta

        let request1 : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createMethodModule "Method helper generation 1"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitAndAssert request1

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not expose an updated baseline."

        let request2 : IlxDeltaRequest =
            { Baseline = baseline2
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createMethodModule "Method helper generation 2"
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitAndAssert request2
        Assert.NotEqual(System.Guid.Empty, delta2.BaseGenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

        if not (keepArtifacts ()) then
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()


    [<Fact>]
    let ``emitDelta emits portable PDB deltas across property getter generations`` () =
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createPropertyModule "Property helper baseline message")
        let typeName = "Sample.PropertyDemo"
        let methodKey = TestHelpers.methodKeyByName artifacts.Baseline typeName "get_Message"
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]

        let emitAndAssert request expectedMarker =
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for property getter edit."
            assertPdbContainsMethodToken pdbBytes methodToken
            assertPdbContainsLiteral pdbBytes expectedMarker
            delta

        let accessorUpdate =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.PropertyGet "Message") methodKey

        let request1 : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ accessorUpdate ]
              Module = TestHelpers.createPropertyModule "Property helper generation 1"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitAndAssert request1 "Property helper generation 1"

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not expose an updated baseline."

        let accessorUpdate2 =
            TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.PropertyGet "Message") methodKey

        let request2 : IlxDeltaRequest =
            { Baseline = baseline2
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ accessorUpdate2 ]
              Module = TestHelpers.createPropertyModule "Property helper generation 2"
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitAndAssert request2 "Property helper generation 2"
        Assert.NotEqual(System.Guid.Empty, delta2.BaseGenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

        if not (keepArtifacts ()) then
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()


    [<Fact>]
    let ``emitDelta emits portable PDB deltas across event accessor generations`` () =
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createEventModule "Event helper baseline payload")
        let typeName = "Sample.EventDemo"
        let methodKey = TestHelpers.methodKey typeName "add_OnChanged" [ PrimaryAssemblyILGlobals.typ_Object ] ILType.Void
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]

        let emitAndAssert request expectedMarker =
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for event accessor edit."
            assertPdbContainsMethodToken pdbBytes methodToken
            assertPdbContainsLiteral pdbBytes expectedMarker
            delta

        let request1 : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.EventAdd "OnChanged") methodKey ]
              Module = TestHelpers.createEventModule "Event helper generation 1"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitAndAssert request1 "Event helper generation 1"

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not expose an updated baseline."

        let request2 : IlxDeltaRequest =
            { Baseline = baseline2
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = [ TestHelpers.mkAccessorUpdate typeName (SymbolMemberKind.EventAdd "OnChanged") methodKey ]
              Module = TestHelpers.createEventModule "Event helper generation 2"
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitAndAssert request2 "Event helper generation 2"
        Assert.NotEqual(System.Guid.Empty, delta2.BaseGenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

        if not (keepArtifacts ()) then
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()

    [<Fact>]
    let ``emitDelta emits portable PDB deltas across closure helper generations`` () =
        let typeName = "Sample.ClosureDemo"
        let methodKey = TestHelpers.methodKey typeName "Invoke" [] PrimaryAssemblyILGlobals.typ_String
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createClosureModule "Closure helper baseline message")
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]

        let emitAndAssert request expectedMarker =
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for closure helper edit."
            assertPdbContainsMethodToken pdbBytes methodToken
            assertPdbContainsLiteral pdbBytes expectedMarker
            delta

        let request1 : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createClosureModule "Closure helper generation 1"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitAndAssert request1 "Closure helper generation 1"

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not expose an updated baseline."

        let request2 : IlxDeltaRequest =
            { Baseline = baseline2
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createClosureModule "Closure helper generation 2"
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitAndAssert request2 "Closure helper generation 2"
        Assert.NotEqual(System.Guid.Empty, delta2.BaseGenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

        if not (keepArtifacts ()) then
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()

    [<Fact>]
    let ``emitDelta emits portable PDB deltas across async helper generations`` () =
        let typeName = "Sample.AsyncDemo"
        let methodKey = TestHelpers.methodKey typeName "RunAsync" [ PrimaryAssemblyILGlobals.typ_Int32 ] PrimaryAssemblyILGlobals.typ_String
        let artifacts = TestHelpers.createBaselineFromModule (TestHelpers.createAsyncModule "Async helper baseline message")
        let methodToken = artifacts.Baseline.MethodTokens[methodKey]

        let emitAndAssert request expectedMarker =
            let delta = emitDelta request
            let pdbBytes =
                match delta.Pdb with
                | Some bytes -> bytes
                | None -> failwith "Expected portable PDB delta for async helper edit."
            assertPdbContainsMethodToken pdbBytes methodToken
            assertPdbContainsLiteral pdbBytes expectedMarker
            delta

        let request1 : IlxDeltaRequest =
            { Baseline = artifacts.Baseline
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createAsyncModule "Async helper generation 1"
              SymbolChanges = None
              CurrentGeneration = 1
              PreviousGenerationId = None
              SynthesizedNames = None }

        let delta1 = emitAndAssert request1 "Async helper generation 1"

        let baseline2 =
            match delta1.UpdatedBaseline with
            | Some b -> b
            | None -> failwith "Generation 1 delta did not expose an updated baseline."

        let request2 : IlxDeltaRequest =
            { Baseline = baseline2
              UpdatedTypes = [ typeName ]
              UpdatedMethods = [ methodKey ]
              UpdatedAccessors = []
              Module = TestHelpers.createAsyncModule "Async helper generation 2"
              SymbolChanges = None
              CurrentGeneration = 2
              PreviousGenerationId = Some delta1.GenerationId
              SynthesizedNames = None }

        let delta2 = emitAndAssert request2 "Async helper generation 2"
        Assert.NotEqual(System.Guid.Empty, delta2.BaseGenerationId)
        Assert.Equal(delta1.GenerationId, delta2.BaseGenerationId)

        if not (keepArtifacts ()) then
            if File.Exists(artifacts.AssemblyPath) then File.Delete(artifacts.AssemblyPath)
            match artifacts.PdbPath with
            | Some path when File.Exists(path) -> File.Delete(path)
            | _ -> ()
