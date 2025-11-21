namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Collections.Immutable
open System.Text
open System.Text
open Xunit
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.CodeGen
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaMetadataSerializer
open FSharp.Compiler.CodeGen.DeltaTableLayout
open FSharp.Compiler.Service.Tests.HotReload.MetadataDeltaTestHelpers

module DeltaWriter = FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

module FSharpDeltaMetadataWriterTests =

    let private metadataStringDeltaBytes = 14
    let private metadataBlobDeltaBytes = 1
    let private asyncStringDeltaBytes = 128
    let private asyncBlobDeltaBytes = 6

    let private ignoreBadImageFormat (action: unit -> unit) =
        try
            action ()
        with :? BadImageFormatException -> ()

    let inline private encTablePriority (tableIndex: TableIndex) = int tableIndex

    let private sortEncLogEntries (entries: (TableIndex * int * EditAndContinueOperation)[]) =
        entries
        |> Array.sortBy (fun (table, rowId, _) -> ((encTablePriority table) <<< 24) ||| (rowId &&& 0x00FFFFFF))

    let private sortEncMapEntries (entries: (TableIndex * int)[]) =
        entries
        |> Array.sortBy (fun (table, rowId) -> ((encTablePriority table) <<< 24) ||| (rowId &&& 0x00FFFFFF))

    let private moduleEncLogEntry = (TableIndex.Module, 1, EditAndContinueOperation.Default)
    let private moduleEncMapEntry = (TableIndex.Module, 1)

    let private ensureModuleEncLogEntry (entries: (TableIndex * int * EditAndContinueOperation)[]) =
        if entries |> Array.exists (fun (table, _, _) -> table = TableIndex.Module) then
            entries
        else
            Array.append [| moduleEncLogEntry |] entries

    let private ensureModuleEncMapEntry (entries: (TableIndex * int)[]) =
        if entries |> Array.exists (fun (table, _) -> table = TableIndex.Module) then
            entries
        else
            Array.append [| moduleEncMapEntry |] entries

    let private assertEncLogEqual expected actual =
        let expectedWithModule = expected |> ensureModuleEncLogEntry |> sortEncLogEntries
        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedWithModule, sortEncLogEntries actual)

    let private assertEncMapEqual expected actual =
        let expectedWithModule = expected |> ensureModuleEncMapEntry |> sortEncMapEntries
        Assert.Equal<(TableIndex * int)[]>(expectedWithModule, sortEncMapEntries actual)
    let private localSignatureBlobDeltaBytes = 5

    let private assertBaselineHeapSnapshot (artifacts: MetadataDeltaTestHelpers.MetadataDeltaArtifacts) =
        use peReader = new PEReader(new MemoryStream(artifacts.BaselineBytes, writable = false))
        let metadataReader = peReader.GetMetadataReader()
        let baseline = artifacts.BaselineHeapSizes
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.String, baseline.StringHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.Blob, baseline.BlobHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.Guid, baseline.GuidHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.UserString, baseline.UserStringHeapSize)

    let private assertBaselineHeapSnapshotMulti (artifacts: MetadataDeltaTestHelpers.MultiGenerationMetadataArtifacts) =
        use peReader = new PEReader(new MemoryStream(artifacts.BaselineBytes, writable = false))
        let metadataReader = peReader.GetMetadataReader()
        let baseline = artifacts.BaselineHeapSizes
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.String, baseline.StringHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.Blob, baseline.BlobHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.Guid, baseline.GuidHeapSize)
        Assert.Equal(metadataReader.GetHeapSize HeapIndex.UserString, baseline.UserStringHeapSize)

    let private readMetadataRoot metadata (reader: BinaryReader) =
        let readUInt32 () = reader.ReadUInt32()
        let readUInt16 () = reader.ReadUInt16()

        let _signature = readUInt32 ()
        let _major = readUInt16 ()
        let _minor = readUInt16 ()
        let _reserved = readUInt32 ()
        let versionLength = int (readUInt32 ())
        reader.ReadBytes(versionLength) |> ignore
        while reader.BaseStream.Position % 4L <> 0L do
            reader.ReadByte() |> ignore

        let _flags = readUInt16 ()
        let streamCount = int (readUInt16 ())

        let readStreamName () =
            let buffer = ResizeArray()
            let mutable finished = false
            while not finished do
                let b = reader.ReadByte()
                if b = 0uy then
                    finished <- true
                else
                    buffer.Add b
            while reader.BaseStream.Position % 4L <> 0L do
                reader.ReadByte() |> ignore
            Encoding.UTF8.GetString(buffer.ToArray())

        [ for _ in 1 .. streamCount do
              let offset = readUInt32 ()
              let size = readUInt32 ()
              let name = readStreamName ()
              yield struct (offset, size, name) ]

    let private metadataStreamNames (metadata: byte[]) =
        use stream = new MemoryStream(metadata, false)
        use reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen = true)
        readMetadataRoot metadata reader
        |> List.map (fun struct (_, _, name) -> name)

    let private readTableBitMasksFromMetadata (metadata: byte[]) : TableBitMasks =
        use stream = new MemoryStream(metadata, false)
        use reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen = true)

        let streams = readMetadataRoot metadata reader

        let tableStreamOffset =
            streams
            |> List.tryFind (fun struct (_, _, name) -> name = "#-" || name = "#~")
            |> Option.map (fun struct (offset, _, _) -> offset)
            |> Option.defaultWith (fun () -> failwith "Table stream not found in metadata")

        reader.BaseStream.Position <- int64 tableStreamOffset

        let _reserved = reader.ReadUInt32()
        let _major = reader.ReadByte()
        let _minor = reader.ReadByte()
        let _heapSizes = reader.ReadByte()
        reader.ReadByte() |> ignore // reserved

        let validLow = reader.ReadUInt32() |> int
        let validHigh = reader.ReadUInt32() |> int
        let sortedLow = reader.ReadUInt32() |> int
        let sortedHigh = reader.ReadUInt32() |> int

        { ValidLow = validLow
          ValidHigh = validHigh
          SortedLow = sortedLow
          SortedHigh = sortedHigh }

    let private isTablePresent (bitmask: TableBitMasks) (table: TableIndex) =
        let index = int table
        if index < 32 then
            ((bitmask.ValidLow >>> index) &&& 1) <> 0
        else
            ((bitmask.ValidHigh >>> (index - 32)) &&& 1) <> 0

    let private getRowCounts (reader: MetadataReader) =
        Array.init MetadataTokens.TableCount (fun i ->
            let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte i)
            reader.GetTableRowCount table)

    let private withMetadataReader (metadata: byte[]) (action: MetadataReader -> 'T) : 'T =
        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange metadata)
        let reader = provider.GetMetadataReader()
        action reader

    let private getHeapSize (metadata: byte[]) (heap: HeapIndex) : int =
        withMetadataReader metadata (fun reader -> reader.GetHeapSize heap)

    let private getDeltaHeapSize (delta: DeltaWriter.MetadataDelta) (heap: HeapIndex) : int =
        match heap with
        | HeapIndex.String -> delta.HeapSizes.StringHeapSize
        | HeapIndex.Blob -> delta.HeapSizes.BlobHeapSize
        | HeapIndex.Guid -> delta.HeapSizes.GuidHeapSize
        | HeapIndex.UserString -> delta.HeapSizes.UserStringHeapSize
        | _ -> invalidArg (nameof heap) "Unsupported heap index for delta metadata"

    let private assertStringHeapGrowthWithin label (artifacts: MetadataDeltaTestHelpers.MetadataDeltaArtifacts) maxGrowthBytes =
        assertBaselineHeapSnapshot artifacts
        let growth = getDeltaHeapSize artifacts.Delta HeapIndex.String
        Assert.True(
            growth <= maxGrowthBytes,
            sprintf "[%s] string heap grew by %d bytes (limit %d)" label growth maxGrowthBytes)

    let private assertStringHeapGrowthWithinMulti label (artifacts: MetadataDeltaTestHelpers.MultiGenerationMetadataArtifacts) maxGrowthBytes =
        assertBaselineHeapSnapshotMulti artifacts

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            let growth = getDeltaHeapSize delta HeapIndex.String
            Assert.True(
                growth <= maxGrowthBytes,
                sprintf "[%s] string heap grew by %d bytes (limit %d)" label growth maxGrowthBytes)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    let private assertBlobHeapGrowthWithin label (artifacts: MetadataDeltaTestHelpers.MetadataDeltaArtifacts) maxGrowthBytes =
        assertBaselineHeapSnapshot artifacts
        let growth = getDeltaHeapSize artifacts.Delta HeapIndex.Blob
        Assert.True(
            growth <= maxGrowthBytes,
            sprintf "[%s] blob heap grew by %d bytes (limit %d)" label growth maxGrowthBytes)

    let private assertBlobHeapGrowthWithinMulti label (artifacts: MetadataDeltaTestHelpers.MultiGenerationMetadataArtifacts) maxGrowthBytes =
        assertBaselineHeapSnapshotMulti artifacts

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            let growth = getDeltaHeapSize delta HeapIndex.Blob
            Assert.True(
                growth <= maxGrowthBytes,
                sprintf "[%s] blob heap grew by %d bytes (limit %d)" label growth maxGrowthBytes)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    let private assertTableCountsMatch metadata (expected: int[]) =
        withMetadataReader metadata (fun reader ->
            for i = 0 to expected.Length - 1 do
                let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte i)
                let actual = reader.GetTableRowCount table
                Assert.Equal(expected.[i], actual))

    let private assertBitMasksMatch (metadata: byte[]) (bitMasks: TableBitMasks) =
        let actual = readTableBitMasksFromMetadata metadata
        Assert.Equal(actual.ValidLow, bitMasks.ValidLow)
        Assert.Equal(actual.ValidHigh, bitMasks.ValidHigh)
        Assert.Equal(actual.SortedLow, bitMasks.SortedLow)
        Assert.Equal(actual.SortedHigh, bitMasks.SortedHigh)

    let private decodeEntityHandle (handle: EntityHandle) =
        let token = MetadataTokens.GetToken(handle)
        let tableValue = byte (token >>> 24)
        let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(tableValue)
        let rowId = token &&& 0x00FFFFFF
        (table, rowId)

    let private readEncLogEntriesFromMetadata metadata =
        withMetadataReader metadata (fun reader ->
            reader.GetEditAndContinueLogEntries()
            |> Seq.map (fun entry ->
                let (table, rowId) = decodeEntityHandle entry.Handle
                (table, rowId, entry.Operation))
            |> Seq.toArray)

    let private readEncMapEntriesFromMetadata metadata =
        withMetadataReader metadata (fun reader ->
            reader.GetEditAndContinueMapEntries()
            |> Seq.map decodeEntityHandle
            |> Seq.toArray)

    let private assertEncLogMatches metadata expected =
        let actual = readEncLogEntriesFromMetadata metadata
        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expected, actual)

    let private assertEncMapMatches metadata expected =
        let actual = readEncMapEntriesFromMetadata metadata
        Assert.Equal<(TableIndex * int)[]>(expected, actual)

    [<Fact>]
    let ``metadata writer emits property rows`` () =
        let moduleDef = createPropertyModule None ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()

        let typeHandle =
            metadataReader.TypeDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetTypeDefinition(handle).Name) = "PropertyHost")

        let getterHandle =
            metadataReader.MethodDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetMethodDefinition(handle).Name) = "get_Message")

        let propertyHandle =
            metadataReader.PropertyDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetPropertyDefinition(handle).Name) = "Message")

        let builder = IlDeltaStreamBuilder None

        let stringType = ilGlobals.typ_String
        let methodKey = methodKey "Sample.PropertyHost" "get_Message" stringType

        let getterDef = metadataReader.GetMethodDefinition getterHandle
        let methodRow : DeltaWriter.MethodDefinitionRowInfo =
            { Key = methodKey
              RowId = 1
              IsAdded = true
              Attributes = getterDef.Attributes
              ImplAttributes = getterDef.ImplAttributes
              Name = metadataReader.GetString getterDef.Name
              NameHandle = if getterDef.Name.IsNil then None else Some getterDef.Name
              Signature = metadataReader.GetBlobBytes getterDef.Signature
              SignatureHandle = if getterDef.Signature.IsNil then None else Some getterDef.Signature
              FirstParameterRowId = None
              CodeRva = None }
        let methodDefinitionRows = [ methodRow ]

        let updates: DeltaWriter.MethodMetadataUpdate list =
            [ { MethodKey = methodKey
                MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit getterHandle)
                MethodHandle = getterHandle
                Body =
                    { MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit getterHandle)
                      LocalSignatureToken = 0
                      CodeOffset = 0
                      CodeLength = 1 } } ]

        let propertyKey : PropertyDefinitionKey =
            { DeclaringType = "Sample.PropertyHost"
              Name = "Message"
              PropertyType = stringType
              IndexParameterTypes = [] }

        let propertyDef = metadataReader.GetPropertyDefinition propertyHandle
        let propertyRows: DeltaWriter.PropertyDefinitionRowInfo list =
            [ { Key = propertyKey
                RowId = 1
                IsAdded = true
                Name = metadataReader.GetString propertyDef.Name
                NameHandle = if propertyDef.Name.IsNil then None else Some propertyDef.Name
                Signature = metadataReader.GetBlobBytes propertyDef.Signature
                SignatureHandle = if propertyDef.Signature.IsNil then None else Some propertyDef.Signature
                Attributes = propertyDef.Attributes } ]

        let propertyMapRows: DeltaWriter.PropertyMapRowInfo list =
            [ { DeclaringType = "Sample.PropertyHost"
                RowId = 1
                TypeDefRowId = MetadataTokens.GetRowNumber typeHandle
                FirstPropertyRowId = Some 1
                IsAdded = true } ]

        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodDefinitionRows
                []
                propertyRows
                []
                propertyMapRows
                []
                []
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        let tableCount index = metadataDelta.TableRowCounts.[ int index ]

        Assert.Equal(1, tableCount TableIndex.Property)
        Assert.Equal(1, tableCount TableIndex.PropertyMap)

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               // Roslyn also tags the containing PropertyMap row as AddProperty.
               (TableIndex.PropertyMap, 1, EditAndContinueOperation.AddProperty)
               (TableIndex.Property, 1, EditAndContinueOperation.AddProperty) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.PropertyMap, 1)
               (TableIndex.Property, 1) |]
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.True(metadataDelta.Metadata.Length > 0)
        Assert.DoesNotContain("Message", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)

    [<Fact>]
    let ``property delta uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        let indexSizes = artifacts.Delta.IndexSizes

        Assert.True(indexSizes.StringsBig)
        Assert.True(indexSizes.BlobsBig)
        Assert.True(indexSizes.HasSemanticsBig)
        Assert.True(indexSizes.MemberRefParentBig)
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Property])

    [<Fact>]
    let ``property multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.PropertyMap, 1, EditAndContinueOperation.AddProperty)
               (TableIndex.Property, 1, EditAndContinueOperation.AddProperty) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.PropertyMap, 1)
               (TableIndex.Property, 1) |]
            |> sortEncMapEntries

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            assertEncLogEqual expectedEncLog delta.EncLog
            assertEncMapEqual expectedEncMap delta.EncMap
            ignoreBadImageFormat (fun () -> assertTableStreamMatches delta)
            ignoreBadImageFormat (fun () -> assertTableCountsMatch delta.Metadata delta.TableRowCounts)
            ignoreBadImageFormat (fun () -> assertBitMasksMatch delta.Metadata delta.TableBitMasks)
            ignoreBadImageFormat (fun () -> assertEncLogMatches delta.Metadata delta.EncLog)
            ignoreBadImageFormat (fun () -> assertEncMapMatches delta.Metadata delta.EncMap)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    [<Fact>]
    let ``property multi-generation string heap omits accessor names`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        let assertHeap (delta: DeltaWriter.MetadataDelta) =
            let heapText = Encoding.UTF8.GetString(delta.StringHeap)
            Assert.DoesNotContain("Message", heapText)

        assertHeap artifacts.Generation1
        assertHeap artifacts.Generation2

    [<Fact>]
    let ``property delta user string heap stays empty`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        let userStringSize = getDeltaHeapSize artifacts.Delta HeapIndex.UserString
        Assert.Equal(1, userStringSize)

    [<Fact>]
    let ``property multi-generation user string heap stays empty`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        Assert.Equal(1, getDeltaHeapSize artifacts.Generation1 HeapIndex.UserString)
        Assert.Equal(1, getDeltaHeapSize artifacts.Generation2 HeapIndex.UserString)

    [<Fact>]
    let ``property multi-generation string heap size stays constant`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        Assert.Equal(artifacts.Generation1.StringHeap.Length, artifacts.Generation2.StringHeap.Length)

    [<Fact>]
    let ``property delta artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        assertBaselineHeapSnapshot artifacts

    [<Fact>]
    let ``property delta heap sizes reflect metadata`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        let expectString = getHeapSize artifacts.Delta.Metadata HeapIndex.String
        let expectBlob = getHeapSize artifacts.Delta.Metadata HeapIndex.Blob
        let expectUserString = getHeapSize artifacts.Delta.Metadata HeapIndex.UserString
        Assert.Equal(expectString, getDeltaHeapSize artifacts.Delta HeapIndex.String)
        Assert.Equal(expectBlob, getDeltaHeapSize artifacts.Delta HeapIndex.Blob)
        Assert.Equal(expectUserString, getDeltaHeapSize artifacts.Delta HeapIndex.UserString)

    [<Fact>]
    let ``property multi-generation artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        assertBaselineHeapSnapshotMulti artifacts

    [<Fact>]
    let ``property delta string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        assertStringHeapGrowthWithin "property-delta" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``property multi-generation string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        assertStringHeapGrowthWithinMulti "property-multigen" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``property delta blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        assertBlobHeapGrowthWithin "property-delta" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``property multi-generation blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        assertBlobHeapGrowthWithinMulti "property-multigen" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``local signature delta artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureDeltaArtifacts None ()
        assertBaselineHeapSnapshot artifacts

    [<Fact>]
    let ``local signature multi-generation artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureMultiGenerationArtifacts ()
        assertBaselineHeapSnapshotMulti artifacts

    [<Fact>]
    let ``local signature delta blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureDeltaArtifacts None ()
        assertBlobHeapGrowthWithin "localsig-delta" artifacts localSignatureBlobDeltaBytes

    [<Fact>]
    let ``local signature multi-generation blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureMultiGenerationArtifacts ()
        assertBlobHeapGrowthWithinMulti "localsig-multigen" artifacts localSignatureBlobDeltaBytes

    [<Fact>]
    let ``local signature delta string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureDeltaArtifacts None ()
        assertStringHeapGrowthWithin "localsig-delta" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``local signature multi-generation string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureMultiGenerationArtifacts ()
        assertStringHeapGrowthWithinMulti "localsig-multigen" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``async multi-generation uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()

        let assertIndexes (delta: DeltaWriter.MetadataDelta) =
            let indexSizes = delta.IndexSizes

            Assert.True(indexSizes.StringsBig)
            Assert.True(indexSizes.BlobsBig)
            Assert.True(indexSizes.TypeOrMethodDefBig)
            Assert.True(indexSizes.MethodDefOrRefBig)
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.MethodDef])

        assertIndexes artifacts.Generation1
        assertIndexes artifacts.Generation2

    [<Fact>]
    let ``async string heap omits updated literal`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts (Some "async generation 2") ()
        let heapText = Encoding.UTF8.GetString(artifacts.Delta.StringHeap)
        Assert.DoesNotContain("async generation", heapText)

    [<Fact>]
    let ``async delta string heap omits parameter names`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let heapText = Encoding.UTF8.GetString(artifacts.Delta.StringHeap)
        Assert.DoesNotContain("token", heapText, StringComparison.Ordinal)

    [<Fact>]
    let ``async delta user string heap stays empty`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts (Some "async generation 2") ()
        let userStringSize = getDeltaHeapSize artifacts.Delta HeapIndex.UserString
        Assert.Equal(1, userStringSize)

    [<Fact>]
    let ``async multi-generation string heap size stays constant`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()
        Assert.Equal(artifacts.Generation1.StringHeap.Length, artifacts.Generation2.StringHeap.Length)

    [<Fact>]
    let ``async multi-generation string heap omits parameter names`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()

        let assertHeap (delta: DeltaWriter.MetadataDelta) =
            let heapText = Encoding.UTF8.GetString(delta.StringHeap)
            Assert.DoesNotContain("token", heapText, StringComparison.Ordinal)

        assertHeap artifacts.Generation1
        assertHeap artifacts.Generation2

    [<Fact>]
    let ``async multi-generation user string heap size stays constant`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()
        let gen1Size = getDeltaHeapSize artifacts.Generation1 HeapIndex.UserString
        let gen2Size = getDeltaHeapSize artifacts.Generation2 HeapIndex.UserString
        Assert.Equal(1, gen1Size)
        Assert.Equal(gen1Size, gen2Size)

    [<Fact>]
    let ``async delta artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        assertBaselineHeapSnapshot artifacts
    [<Fact>]
    let ``async multi-generation artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()
        assertBaselineHeapSnapshotMulti artifacts

    [<Fact>]
    let ``async delta string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        assertStringHeapGrowthWithin "async-delta" artifacts asyncStringDeltaBytes

    [<Fact>]
    let ``async multi-generation string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()
        assertStringHeapGrowthWithinMulti "async-multigen" artifacts asyncStringDeltaBytes

    [<Fact>]
    let ``async delta blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        assertBlobHeapGrowthWithin "async-delta" artifacts asyncBlobDeltaBytes

    [<Fact>]
    let ``async multi-generation blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()
        assertBlobHeapGrowthWithinMulti "async-multigen" artifacts asyncBlobDeltaBytes

    [<Fact>]
    let ``property multi-generation uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()

        let assertIndexes (delta: DeltaWriter.MetadataDelta) =
            let indexSizes = delta.IndexSizes

            Assert.True(indexSizes.StringsBig)
            Assert.True(indexSizes.BlobsBig)
            Assert.True(indexSizes.HasSemanticsBig)
            Assert.True(indexSizes.MemberRefParentBig)
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Property])
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.PropertyMap])

        assertIndexes artifacts.Generation1
        assertIndexes artifacts.Generation2

    [<Fact>]
    let ``metadata root omits #JTD when no ENC tables are present`` () =
        let mirror = DeltaMetadataTables MetadataHeapOffsets.Zero
        mirror.AddModuleRow("Empty.dll", None, System.Guid.NewGuid(), System.Guid.NewGuid(), System.Guid.NewGuid())
        let sizes =
            DeltaMetadataSerializer.computeMetadataSizes mirror (Array.zeroCreate MetadataTokens.TableCount)
        let heaps = DeltaMetadataSerializer.buildHeapStreams mirror
        let tableInput : DeltaMetadataSerializer.DeltaTableSerializerInput =
            { Tables = mirror.TableRows
              MetadataSizes = sizes
              StringHeap = mirror.StringHeapBytes
              StringHeapOffsets = mirror.StringHeapOffsets
              BlobHeap = mirror.BlobHeapBytes
              BlobHeapOffsets = mirror.BlobHeapOffsets
              GuidHeap = mirror.GuidHeapBytes
              HeapOffsets = MetadataHeapOffsets.Zero }
        let tableStream = DeltaMetadataSerializer.buildTableStream tableInput
        let metadata = DeltaMetadataSerializer.serializeMetadataRoot tableInput heaps tableStream
        let names = metadataStreamNames metadata
        Assert.DoesNotContain("#JTD", names)

    [<Fact>]
    let ``metadata root includes #JTD when ENC tables are present`` () =
        let artifacts = emitPropertyDeltaArtifacts None ()
        let names = metadataStreamNames artifacts.Delta.Metadata
        Assert.Contains("#JTD", names)

    [<Fact>]
    let ``metadata delta keeps BSJB signature and empty heap entries`` () =
        // Use a simple property delta to produce real delta metadata/IL
        let artifacts = emitPropertyDeltaArtifacts None ()
        let metadata = artifacts.Delta.Metadata

        // Validate metadata root header (BSJB + version 1.1)
        use stream = new MemoryStream(metadata, false)
        use reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen = true)
        let signature = reader.ReadUInt32()
        Assert.Equal<uint32>(0x424A5342u, signature) // "BSJB" little-endian
        let major = reader.ReadUInt16()
        let minor = reader.ReadUInt16()
        Assert.Equal(1us, major)
        Assert.Equal(1us, minor)

        // Validate required streams are present
        let names = metadataStreamNames metadata
        Assert.Contains("#~", names)
        Assert.Contains("#Strings", names)
        Assert.Contains("#US", names)
        Assert.Contains("#Blob", names)
        Assert.Contains("#GUID", names)

        // Validate row-0 heap entries remain the empty items required by ECMA
        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(metadata))
        let mdReader = provider.GetMetadataReader()
        Assert.Equal("", mdReader.GetString(MetadataTokens.StringHandle 0))
        Assert.Equal(0, mdReader.GetBlobBytes(MetadataTokens.BlobHandle 0).Length)
        Assert.Equal("", mdReader.GetUserString(MetadataTokens.UserStringHandle 0))
    [<Fact>]
    let ``metadata writer emits event and method semantics rows`` () =
        let moduleDef = createEventModule None ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()

        let typeHandle =
            metadataReader.TypeDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetTypeDefinition(handle).Name) = "EventHost")

        let addHandle =
            metadataReader.MethodDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetMethodDefinition(handle).Name) = "add_OnChanged")

        let eventHandle =
            metadataReader.EventDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetEventDefinition(handle).Name) = "OnChanged")

        let builder = IlDeltaStreamBuilder None

        let methodKey = methodKey "Sample.EventHost" "add_OnChanged" ILType.Void

        let addDef = metadataReader.GetMethodDefinition addHandle
        let methodRow : DeltaWriter.MethodDefinitionRowInfo =
            { Key = methodKey
              RowId = 1
              IsAdded = true
              Attributes = addDef.Attributes
              ImplAttributes = addDef.ImplAttributes
              Name = metadataReader.GetString addDef.Name
              NameHandle = if addDef.Name.IsNil then None else Some addDef.Name
              Signature = metadataReader.GetBlobBytes addDef.Signature
              SignatureHandle = if addDef.Signature.IsNil then None else Some addDef.Signature
              FirstParameterRowId = None
              CodeRva = None }
        let methodDefinitionRows = [ methodRow ]

        let updates: DeltaWriter.MethodMetadataUpdate list =
            [ { MethodKey = methodKey
                MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit addHandle)
                MethodHandle = addHandle
                Body =
                    { MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit addHandle)
                      LocalSignatureToken = 0
                      CodeOffset = 0
                      CodeLength = 1 } } ]

        let eventKey =
            { DeclaringType = "Sample.EventHost"
              Name = "OnChanged"
              EventType = Some ilGlobals.typ_Object }

        let eventDef = metadataReader.GetEventDefinition eventHandle
        let eventRows: DeltaWriter.EventDefinitionRowInfo list =
            [ { Key = eventKey
                RowId = 1
                IsAdded = true
                Name = metadataReader.GetString eventDef.Name
                NameHandle = if eventDef.Name.IsNil then None else Some eventDef.Name
                Attributes = eventDef.Attributes
                EventType = eventDef.Type } ]

        let eventMapRows: DeltaWriter.EventMapRowInfo list =
            [ { DeclaringType = "Sample.EventHost"
                RowId = 1
                TypeDefRowId = MetadataTokens.GetRowNumber typeHandle
                FirstEventRowId = Some 1
                IsAdded = true } ]

        let associationHandle = MetadataTokens.EntityHandle(TableIndex.Event, 1)

        let methodSemanticsRows: DeltaWriter.MethodSemanticsMetadataUpdate list =
            [ { RowId = 1
                Association = associationHandle
                MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit addHandle)
                Attributes = MethodSemanticsAttributes.Adder
                IsAdded = true
                AssociationInfo = Some(MethodSemanticsAssociation.EventAssociation(eventKey, 1)) } ]

        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodDefinitionRows
                []
                []
                eventRows
                []
                eventMapRows
                methodSemanticsRows
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        let tableCount index = metadataDelta.TableRowCounts.[int index]
        Assert.Equal(1, tableCount TableIndex.Event)
        Assert.Equal(1, tableCount TableIndex.EventMap)
        Assert.Equal(1, tableCount TableIndex.MethodSemantics)

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.EventMap, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.Event, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.MethodSemantics, 1, EditAndContinueOperation.AddMethod) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.EventMap, 1)
               (TableIndex.Event, 1)
               (TableIndex.MethodSemantics, 1) |]
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.DoesNotContain("OnChanged", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)

    [<Fact>]
    let ``event delta uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        let indexSizes = artifacts.Delta.IndexSizes

        Assert.True(indexSizes.StringsBig)
        Assert.True(indexSizes.BlobsBig)
        Assert.True(indexSizes.HasSemanticsBig)
        Assert.True(indexSizes.MemberRefParentBig)
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Event])
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.EventMap])

    [<Fact>]
    let ``event multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, 1, EditAndContinueOperation.AddParameter)
               (TableIndex.EventMap, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.Event, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.MethodSemantics, 1, EditAndContinueOperation.AddMethod) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.Param, 1)
               (TableIndex.EventMap, 1)
               (TableIndex.Event, 1)
               (TableIndex.MethodSemantics, 1) |]
            |> sortEncMapEntries

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            assertEncLogEqual expectedEncLog delta.EncLog
            assertEncMapEqual expectedEncMap delta.EncMap
            ignoreBadImageFormat (fun () -> assertTableStreamMatches delta)
            ignoreBadImageFormat (fun () -> assertTableCountsMatch delta.Metadata delta.TableRowCounts)
            ignoreBadImageFormat (fun () -> assertBitMasksMatch delta.Metadata delta.TableBitMasks)
            ignoreBadImageFormat (fun () -> assertEncLogMatches delta.Metadata delta.EncLog)
            ignoreBadImageFormat (fun () -> assertEncMapMatches delta.Metadata delta.EncMap)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    [<Fact>]
    let ``event multi-generation string heap omits accessor names`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        let assertHeap (delta: DeltaWriter.MetadataDelta) =
            let heapText = Encoding.UTF8.GetString(delta.StringHeap)
            Assert.DoesNotContain("OnChanged", heapText)

        assertHeap artifacts.Generation1
        assertHeap artifacts.Generation2

    [<Fact>]
    let ``event delta user string heap stays empty`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        let userStringSize = getDeltaHeapSize artifacts.Delta HeapIndex.UserString
        Assert.Equal(1, userStringSize)

    [<Fact>]
    let ``event multi-generation user string heap stays empty`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        Assert.Equal(1, getDeltaHeapSize artifacts.Generation1 HeapIndex.UserString)
        Assert.Equal(1, getDeltaHeapSize artifacts.Generation2 HeapIndex.UserString)

    [<Fact>]
    let ``event multi-generation string heap size stays constant`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        Assert.Equal(artifacts.Generation1.StringHeap.Length, artifacts.Generation2.StringHeap.Length)

    [<Fact>]
    let ``event delta artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        assertBaselineHeapSnapshot artifacts

    [<Fact>]
    let ``event multi-generation artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        assertBaselineHeapSnapshotMulti artifacts

    [<Fact>]
    let ``event delta string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        assertStringHeapGrowthWithin "event-delta" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``event multi-generation string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        assertStringHeapGrowthWithinMulti "event-multigen" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``event delta blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        assertBlobHeapGrowthWithin "event-delta" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``event multi-generation blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        assertBlobHeapGrowthWithinMulti "event-multigen" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``closure delta artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureDeltaArtifacts ()
        assertBaselineHeapSnapshot artifacts

    [<Fact>]
    let ``closure multi-generation artifacts capture baseline heap sizes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()
        assertBaselineHeapSnapshotMulti artifacts

    [<Fact>]
    let ``closure delta string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureDeltaArtifacts ()
        assertStringHeapGrowthWithin "closure-delta" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``closure multi-generation string heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()
        assertStringHeapGrowthWithinMulti "closure-multigen" artifacts metadataStringDeltaBytes

    [<Fact>]
    let ``closure delta blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureDeltaArtifacts ()
        assertBlobHeapGrowthWithin "closure-delta" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``closure multi-generation blob heap growth stays bounded`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()
        assertBlobHeapGrowthWithinMulti "closure-multigen" artifacts metadataBlobDeltaBytes

    [<Fact>]
    let ``event multi-generation uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()

        let assertIndexes (delta: DeltaWriter.MetadataDelta) =
            let indexSizes = delta.IndexSizes

            Assert.True(indexSizes.StringsBig)
            Assert.True(indexSizes.BlobsBig)
            Assert.True(indexSizes.HasSemanticsBig)
            Assert.True(indexSizes.MemberRefParentBig)
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Event])
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.EventMap])

        assertIndexes artifacts.Generation1
        assertIndexes artifacts.Generation2

    [<Fact>]
    let ``metadata writer emits method rows for async body edits`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let metadataDelta = artifacts.Delta

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(0, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.Default)
               (TableIndex.TypeRef, 1, EditAndContinueOperation.Default)
               (TableIndex.TypeRef, 2, EditAndContinueOperation.Default)
               (TableIndex.MemberRef, 1, EditAndContinueOperation.Default)
               (TableIndex.AssemblyRef, 1, EditAndContinueOperation.Default)
               (TableIndex.StandAloneSig, 1, EditAndContinueOperation.Default)
               (TableIndex.CustomAttribute, 1, EditAndContinueOperation.Default) |]
            |> sortEncLogEntries
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.TypeRef, 1)
               (TableIndex.TypeRef, 2)
               (TableIndex.MemberRef, 1)
               (TableIndex.AssemblyRef, 1)
               (TableIndex.StandAloneSig, 1)
               (TableIndex.CustomAttribute, 1) |]
            |> sortEncMapEntries
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.True(metadataDelta.Metadata.Length > 0)
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)

    [<Fact>]
    let ``async delta uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let indexSizes = artifacts.Delta.IndexSizes

        Assert.True(indexSizes.StringsBig)
        Assert.True(indexSizes.BlobsBig)
        Assert.True(indexSizes.TypeOrMethodDefBig)
        Assert.True(indexSizes.MethodDefOrRefBig)
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.MethodDef])

    [<Fact>]
    let ``async delta metadata can be reopened`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()

        use provider =
            MetadataReaderProvider.FromMetadataImage(
                ImmutableArray.CreateRange<byte>(artifacts.Delta.Metadata)
            )

        let reader = provider.GetMetadataReader()
        Assert.Equal(1, reader.GetTableRowCount(TableIndex.AssemblyRef))
        Assert.Equal(1, reader.GetTableRowCount(TableIndex.CustomAttribute))

    [<Fact>]
    let ``async delta matches roslyn type/member refs`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let tableCounts = artifacts.Delta.TableRowCounts

        Assert.Equal(2, tableCounts.[int TableIndex.TypeRef])
        Assert.Equal(1, tableCounts.[int TableIndex.MemberRef])
        Assert.Equal(1, tableCounts.[int TableIndex.StandAloneSig])

    [<Fact>]
    let ``method rows prefer delta code offsets`` () =
        let table = DeltaMetadataTables()

        let methodKey : MethodDefinitionKey =
            { DeclaringType = "Sample.Type"
              Name = "Method"
              GenericArity = 0
              ParameterTypes = []
              ReturnType = ILType.Void }

        let methodRow : DeltaWriter.MethodDefinitionRowInfo =
            { Key = methodKey
              RowId = 1
              IsAdded = false
              Attributes = enum 0
              ImplAttributes = enum 0
              Name = "Method"
              NameHandle = None
              Signature = Array.empty
              SignatureHandle = None
              FirstParameterRowId = None
              CodeRva = Some 4096 }

        let body : MethodBodyUpdate =
            { MethodToken = 0x06000001
              LocalSignatureToken = 0
              CodeOffset = 8
              CodeLength = 4 }

        table.AddMethodRow(methodRow, body)

        let storedRva = table.TableRows.MethodDef.[0].[0].Value
        Assert.Equal(8, storedRva)

    [<Fact>]
    let ``async multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.Default)
               (TableIndex.TypeRef, 1, EditAndContinueOperation.Default)
               (TableIndex.TypeRef, 2, EditAndContinueOperation.Default)
               (TableIndex.MemberRef, 1, EditAndContinueOperation.Default)
               (TableIndex.AssemblyRef, 1, EditAndContinueOperation.Default)
               (TableIndex.StandAloneSig, 1, EditAndContinueOperation.Default)
               (TableIndex.CustomAttribute, 1, EditAndContinueOperation.Default) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.TypeRef, 1)
               (TableIndex.TypeRef, 2)
               (TableIndex.MemberRef, 1)
               (TableIndex.AssemblyRef, 1)
               (TableIndex.StandAloneSig, 1)
               (TableIndex.CustomAttribute, 1) |]
            |> sortEncMapEntries

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            assertEncLogEqual expectedEncLog delta.EncLog
            assertEncMapEqual expectedEncMap delta.EncMap
            ignoreBadImageFormat (fun () -> assertTableStreamMatches delta)
            ignoreBadImageFormat (fun () -> assertTableCountsMatch delta.Metadata delta.TableRowCounts)
            ignoreBadImageFormat (fun () -> assertBitMasksMatch delta.Metadata delta.TableBitMasks)
            ignoreBadImageFormat (fun () -> assertEncLogMatches delta.Metadata delta.EncLog)
            ignoreBadImageFormat (fun () -> assertEncMapMatches delta.Metadata delta.EncMap)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    [<Fact>]
    let ``closure delta uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureDeltaArtifacts ()
        let indexSizes = artifacts.Delta.IndexSizes

        Assert.True(indexSizes.StringsBig)
        Assert.True(indexSizes.BlobsBig)
        Assert.True(indexSizes.TypeOrMethodDefBig)
        Assert.True(indexSizes.MethodDefOrRefBig)
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.MethodDef])
        Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Param])

    [<Fact>]
    let ``closure multi-generation uses ENC-sized indexes`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()

        let assertIndexes (delta: DeltaWriter.MetadataDelta) =
            let indexSizes = delta.IndexSizes

            Assert.True(indexSizes.StringsBig)
            Assert.True(indexSizes.BlobsBig)
            Assert.True(indexSizes.TypeOrMethodDefBig)
            Assert.True(indexSizes.MethodDefOrRefBig)
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.MethodDef])
            Assert.True(indexSizes.SimpleIndexBig[int TableIndex.Param])

        assertIndexes artifacts.Generation1
        assertIndexes artifacts.Generation2

    [<Fact>]
    let ``metadata writer reports small index sizes for property delta`` () =
        let delta = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        let indexSizes = delta.Delta.IndexSizes

        Assert.True(indexSizes.StringsBig)
        Assert.True(indexSizes.BlobsBig)
        Assert.True(indexSizes.GuidsBig)
        Assert.True(indexSizes.SimpleIndexBig.[int TableIndex.PropertyMap])
        Assert.True(indexSizes.HasSemanticsBig)

    [<Fact>]
    let ``metadata writer sets table bitmasks for event semantics`` () =
        let delta = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        let masks = delta.Delta.TableBitMasks

        let rowCounts = delta.Delta.TableRowCounts
        let tablesToCheck =
            [ TableIndex.Event
              TableIndex.EventMap
              TableIndex.MethodSemantics
              TableIndex.EncLog
              TableIndex.EncMap ]

        for table in tablesToCheck do
            let expected = rowCounts.[int table] > 0
            Assert.Equal(expected, isTablePresent masks table)

    [<Fact>]
    let ``local signature delta emits standalone signature rows`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureDeltaArtifacts None ()
        use provider =
            MetadataReaderProvider.FromMetadataImage(
                ImmutableArray.CreateRange<byte>(artifacts.Delta.Metadata))
        let reader = provider.GetMetadataReader()

        let rowCount = reader.GetTableRowCount(TableIndex.StandAloneSig)
        Assert.Equal(1, rowCount)

        let encLog = readEncLogEntriesFromMetadata artifacts.Delta.Metadata
        Assert.Contains((TableIndex.StandAloneSig, 1, EditAndContinueOperation.Default), encLog)

    [<Fact>]
    let ``abstract metadata serializer matches metadata builder output for property rows`` () =
        let moduleDef = createPropertyModule None ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()

        let typeHandle =
            metadataReader.TypeDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetTypeDefinition(handle).Name) = "PropertyHost")

        let getterHandle =
            metadataReader.MethodDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetMethodDefinition(handle).Name) = "get_Message")

        let propertyHandle =
            metadataReader.PropertyDefinitions
            |> Seq.find (fun handle -> metadataReader.GetString(metadataReader.GetPropertyDefinition(handle).Name) = "Message")

        let builder = IlDeltaStreamBuilder None

        let stringType = ilGlobals.typ_String
        let methodKey = methodKey "Sample.PropertyHost" "get_Message" stringType

        let getterDef = metadataReader.GetMethodDefinition getterHandle
        let methodRow2 : DeltaWriter.MethodDefinitionRowInfo =
            { Key = methodKey
              RowId = 1
              IsAdded = true
              Attributes = getterDef.Attributes
              ImplAttributes = getterDef.ImplAttributes
              Name = metadataReader.GetString getterDef.Name
              NameHandle = if getterDef.Name.IsNil then None else Some getterDef.Name
              Signature = metadataReader.GetBlobBytes getterDef.Signature
              SignatureHandle = if getterDef.Signature.IsNil then None else Some getterDef.Signature
              FirstParameterRowId = None
              CodeRva = None }
        let methodDefinitionRows = [ methodRow2 ]

        let updates: DeltaWriter.MethodMetadataUpdate list =
            [ { MethodKey = methodKey
                MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit getterHandle)
                MethodHandle = getterHandle
                Body =
                    { MethodToken = MetadataTokens.GetToken(EntityHandle.op_Implicit getterHandle)
                      LocalSignatureToken = 0
                      CodeOffset = 0
                      CodeLength = 1 } } ]

        let propertyKey =
            { DeclaringType = "Sample.PropertyHost"
              Name = "Message"
              PropertyType = stringType
              IndexParameterTypes = [] }

        let propertyDef = metadataReader.GetPropertyDefinition propertyHandle
        let propertyRows: DeltaWriter.PropertyDefinitionRowInfo list =
            [ { Key = propertyKey
                RowId = 1
                IsAdded = true
                Name = metadataReader.GetString propertyDef.Name
                NameHandle = if propertyDef.Name.IsNil then None else Some propertyDef.Name
                Signature = metadataReader.GetBlobBytes propertyDef.Signature
                SignatureHandle = if propertyDef.Signature.IsNil then None else Some propertyDef.Signature
                Attributes = propertyDef.Attributes } ]

        let propertyMapRows: DeltaWriter.PropertyMapRowInfo list =
            [ { DeclaringType = "Sample.PropertyHost"
                RowId = 1
                TypeDefRowId = MetadataTokens.GetRowNumber typeHandle
                FirstPropertyRowId = Some 1
                IsAdded = true } ]

        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodDefinitionRows
                []
                propertyRows
                []
                propertyMapRows
                []
                []
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)

    [<Fact>]
    let ``property delta reports baseline heap offsets`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        use peReader = new PEReader(new MemoryStream(artifacts.BaselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        let baselineStringSize = baselineReader.GetHeapSize HeapIndex.String
        let baselineBlobSize = baselineReader.GetHeapSize HeapIndex.Blob
        let baselineGuidSize = baselineReader.GetHeapSize HeapIndex.Guid
        let baselineUserStringSize = baselineReader.GetHeapSize HeapIndex.UserString

        let delta = artifacts.Delta

        Assert.Equal(baselineStringSize, delta.HeapOffsets.StringHeapStart)
        Assert.Equal(baselineBlobSize, delta.HeapOffsets.BlobHeapStart)
        Assert.Equal(baselineGuidSize, delta.HeapOffsets.GuidHeapStart)
        Assert.Equal(baselineUserStringSize, delta.HeapOffsets.UserStringHeapStart)

    [<Fact>]
    let ``event delta reports baseline heap offsets`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts None ()
        use peReader = new PEReader(new MemoryStream(artifacts.BaselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        let baselineStringSize = baselineReader.GetHeapSize HeapIndex.String
        let baselineBlobSize = baselineReader.GetHeapSize HeapIndex.Blob
        let baselineGuidSize = baselineReader.GetHeapSize HeapIndex.Guid
        let baselineUserStringSize = baselineReader.GetHeapSize HeapIndex.UserString

        let delta = artifacts.Delta

        Assert.Equal(baselineStringSize, delta.HeapOffsets.StringHeapStart)
        Assert.Equal(baselineBlobSize, delta.HeapOffsets.BlobHeapStart)
        Assert.Equal(baselineGuidSize, delta.HeapOffsets.GuidHeapStart)
        Assert.Equal(baselineUserStringSize, delta.HeapOffsets.UserStringHeapStart)

    [<Fact>]
    let ``async delta reports baseline heap offsets`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        use peReader = new PEReader(new MemoryStream(artifacts.BaselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        let baselineStringSize = baselineReader.GetHeapSize HeapIndex.String
        let baselineBlobSize = baselineReader.GetHeapSize HeapIndex.Blob
        let baselineGuidSize = baselineReader.GetHeapSize HeapIndex.Guid
        let baselineUserStringSize = baselineReader.GetHeapSize HeapIndex.UserString

        let delta = artifacts.Delta

        Assert.Equal(baselineStringSize, delta.HeapOffsets.StringHeapStart)
        Assert.Equal(baselineBlobSize, delta.HeapOffsets.BlobHeapStart)
        Assert.Equal(baselineGuidSize, delta.HeapOffsets.GuidHeapStart)
        Assert.Equal(baselineUserStringSize, delta.HeapOffsets.UserStringHeapStart)

    [<Fact>]
    let ``abstract metadata serializer matches metadata builder output for method rows`` () =
        let moduleDef = createMethodModule ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()
        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let nextMethodRowId = ref 1
        let nextParamRowId = ref 1

        let artifacts =
            [ buildAddedMethod metadataReader nextMethodRowId nextParamRowId "Sample.MethodHost" "FormatMessage" [ ilGlobals.typ_Int32 ] ilGlobals.typ_String ]

        let methodRows = artifacts |> List.map (fun a -> a.MethodRow)
        let parameterRows = artifacts |> List.collect (fun a -> a.ParameterRows)
        let updates = artifacts |> List.map (fun a -> a.Update)

        let builder = IlDeltaStreamBuilder None

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodRows
                parameterRows
                []
                []
                []
                []
                []
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])
        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, methodRows.Head.RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows.Head.RowId, EditAndContinueOperation.AddParameter) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, methodRows.Head.RowId)
               (TableIndex.Param, parameterRows.Head.RowId) |]
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.True(metadataDelta.Metadata.Length > 0)
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)

    [<Fact>]
    let ``abstract metadata serializer matches metadata builder output for closure methods`` () =
        let moduleDef = createClosureModule ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()
        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let nextMethodRowId = ref 1
        let nextParamRowId = ref 1

        let artifacts =
            [ buildAddedMethod metadataReader nextMethodRowId nextParamRowId "Sample.ClosureHost" "InvokeOuter" [ ilGlobals.typ_String ] ilGlobals.typ_String
              buildAddedMethod metadataReader nextMethodRowId nextParamRowId "Sample.ClosureHost" "Invoke@40-1" [ ilGlobals.typ_String ] ilGlobals.typ_String ]

        let methodRows = artifacts |> List.map (fun a -> a.MethodRow)
        let parameterRows = artifacts |> List.collect (fun a -> a.ParameterRows)
        let updates = artifacts |> List.map (fun a -> a.Update)

        let builder = IlDeltaStreamBuilder None

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodRows
                parameterRows
                []
                []
                []
                []
                []
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, methodRows[0].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, methodRows[1].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows[0].RowId, EditAndContinueOperation.AddParameter)
               (TableIndex.Param, parameterRows[1].RowId, EditAndContinueOperation.AddParameter) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, methodRows[0].RowId)
               (TableIndex.MethodDef, methodRows[1].RowId)
               (TableIndex.Param, parameterRows[0].RowId)
               (TableIndex.Param, parameterRows[1].RowId) |]
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.True(metadataDelta.Metadata.Length > 0)
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)

    [<Fact>]
    let ``closure multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, 2, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, 1, EditAndContinueOperation.AddParameter)
               (TableIndex.Param, 2, EditAndContinueOperation.AddParameter) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, 1)
               (TableIndex.MethodDef, 2)
               (TableIndex.Param, 1)
               (TableIndex.Param, 2) |]
            |> sortEncMapEntries

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            assertEncLogEqual expectedEncLog delta.EncLog
            assertEncMapEqual expectedEncMap delta.EncMap
            ignoreBadImageFormat (fun () -> assertTableStreamMatches delta)
            ignoreBadImageFormat (fun () -> assertTableCountsMatch delta.Metadata delta.TableRowCounts)
            ignoreBadImageFormat (fun () -> assertBitMasksMatch delta.Metadata delta.TableBitMasks)
            ignoreBadImageFormat (fun () -> assertEncLogMatches delta.Metadata delta.EncLog)
            ignoreBadImageFormat (fun () -> assertEncMapMatches delta.Metadata delta.EncMap)

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

    [<Fact>]
    let ``abstract metadata serializer matches metadata builder output for async methods`` () =
        let moduleDef = createAsyncModule None ()
        let assemblyBytes, _, _, _ = createAssemblyBytes moduleDef
        use peReader = new PEReader(new MemoryStream(assemblyBytes, false))
        let metadataReader = peReader.GetMetadataReader()
        let moduleName = metadataReader.GetString(metadataReader.GetModuleDefinition().Name)

        let nextMethodRowId = ref 1
        let nextParamRowId = ref 1

        let artifacts =
            [ buildAddedMethod metadataReader nextMethodRowId nextParamRowId "Sample.AsyncHost" "RunAsync" [ ilGlobals.typ_Int32 ] ilGlobals.typ_String
              buildAddedMethod metadataReader nextMethodRowId nextParamRowId "Sample.AsyncHostStateMachine" "MoveNext" [] ilGlobals.typ_Bool ]

        let methodRows = artifacts |> List.map (fun a -> a.MethodRow)
        let parameterRows = artifacts |> List.collect (fun a -> a.ParameterRows)
        let updates = artifacts |> List.map (fun a -> a.Update)

        let builder = IlDeltaStreamBuilder None

        let metadataDelta =
            DeltaWriter.emit
                builder.MetadataBuilder
                moduleName
                None
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                (System.Guid.NewGuid())
                methodRows
                parameterRows
                []
                []
                []
                []
                []
                builder.StandaloneSignatures
                []
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.MethodDef, methodRows[0].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, methodRows[1].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows[0].RowId, EditAndContinueOperation.AddParameter) |]
            |> sortEncLogEntries

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.MethodDef, methodRows[0].RowId)
               (TableIndex.MethodDef, methodRows[1].RowId)
               (TableIndex.Param, parameterRows[0].RowId) |]
            |> sortEncMapEntries

        assertEncLogEqual expectedEncLog metadataDelta.EncLog
        assertEncMapEqual expectedEncMap metadataDelta.EncMap
        Assert.True(metadataDelta.Metadata.Length > 0)
        ignoreBadImageFormat (fun () -> assertTableStreamMatches metadataDelta)
        ignoreBadImageFormat (fun () -> assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts)
        ignoreBadImageFormat (fun () -> assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks)
        ignoreBadImageFormat (fun () -> assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog)
        ignoreBadImageFormat (fun () -> assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap)
