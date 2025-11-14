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
        let methodDefinitionRows: DeltaWriter.MethodDefinitionRowInfo list =
            [ { Key = methodKey
                RowId = 1
                IsAdded = true
                Attributes = getterDef.Attributes
                ImplAttributes = getterDef.ImplAttributes
                Name = metadataReader.GetString getterDef.Name
                NameHandle = if getterDef.Name.IsNil then None else Some getterDef.Name
                Signature = metadataReader.GetBlobBytes getterDef.Signature
                SignatureHandle = if getterDef.Signature.IsNil then None else Some getterDef.Signature
                FirstParameterRowId = None } ]

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        let tableCount index = metadataDelta.TableRowCounts.[ int index ]

        Assert.Equal(1, tableCount TableIndex.Property)
        Assert.Equal(1, tableCount TableIndex.PropertyMap)

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.Property, 1, EditAndContinueOperation.AddProperty)
               // Roslyn also tags the containing PropertyMap row as AddProperty.
               (TableIndex.PropertyMap, 1, EditAndContinueOperation.AddProperty) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1)
               (TableIndex.Property, 1)
               (TableIndex.PropertyMap, 1) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        Assert.Contains("Message", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap

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
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.Property, 1, EditAndContinueOperation.AddProperty)
               (TableIndex.PropertyMap, 1, EditAndContinueOperation.AddProperty) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1)
               (TableIndex.Property, 1)
               (TableIndex.PropertyMap, 1) |]

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)
            Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)
            assertTableStreamMatches delta
            assertTableCountsMatch delta.Metadata delta.TableRowCounts
            assertBitMasksMatch delta.Metadata delta.TableBitMasks
            assertEncLogMatches delta.Metadata delta.EncLog
            assertEncMapMatches delta.Metadata delta.EncMap

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

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
        mirror.AddModuleRow("Empty.dll", System.Guid.NewGuid(), System.Guid.NewGuid(), System.Guid.NewGuid())
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
              GuidHeap = mirror.GuidHeapBytes }
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
        let methodDefinitionRows: DeltaWriter.MethodDefinitionRowInfo list =
            [ { Key = methodKey
                RowId = 1
                IsAdded = true
                Attributes = addDef.Attributes
                ImplAttributes = addDef.ImplAttributes
                Name = metadataReader.GetString addDef.Name
                NameHandle = if addDef.Name.IsNil then None else Some addDef.Name
                Signature = metadataReader.GetBlobBytes addDef.Signature
                SignatureHandle = if addDef.Signature.IsNil then None else Some addDef.Signature
                FirstParameterRowId = None } ]

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        let tableCount index = metadataDelta.TableRowCounts.[int index]
        Assert.Equal(1, tableCount TableIndex.Event)
        Assert.Equal(1, tableCount TableIndex.EventMap)
        Assert.Equal(1, tableCount TableIndex.MethodSemantics)

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.Event, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.EventMap, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.MethodSemantics, 1, EditAndContinueOperation.AddMethod) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1)
               (TableIndex.Event, 1)
               (TableIndex.EventMap, 1)
               (TableIndex.MethodSemantics, 1) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.Contains("OnChanged", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap

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
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, 1, EditAndContinueOperation.AddParameter)
               (TableIndex.Event, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.EventMap, 1, EditAndContinueOperation.AddEvent)
               (TableIndex.MethodSemantics, 1, EditAndContinueOperation.AddMethod) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1)
               (TableIndex.Param, 1)
               (TableIndex.Event, 1)
               (TableIndex.EventMap, 1)
               (TableIndex.MethodSemantics, 1) |]

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)
            Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)
            assertTableStreamMatches delta
            assertTableCountsMatch delta.Metadata delta.TableRowCounts
            assertBitMasksMatch delta.Metadata delta.TableBitMasks
            assertEncLogMatches delta.Metadata delta.EncLog
            assertEncMapMatches delta.Metadata delta.EncMap

        assertDelta artifacts.Generation1
        assertDelta artifacts.Generation2

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
    let ``metadata writer emits async method rows`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let metadataDelta = artifacts.Delta

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(0, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.Default) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap

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
    let ``async multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.Default) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1) |]

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)
            Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)
            assertTableStreamMatches delta
            assertTableCountsMatch delta.Metadata delta.TableRowCounts
            assertBitMasksMatch delta.Metadata delta.TableBitMasks
            assertEncLogMatches delta.Metadata delta.EncLog
            assertEncMapMatches delta.Metadata delta.EncMap

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
        let methodDefinitionRows: DeltaWriter.MethodDefinitionRowInfo list =
            [ { Key = methodKey
                RowId = 1
                IsAdded = true
                Attributes = getterDef.Attributes
                ImplAttributes = getterDef.ImplAttributes
                Name = metadataReader.GetString getterDef.Name
                NameHandle = if getterDef.Name.IsNil then None else Some getterDef.Name
                Signature = metadataReader.GetBlobBytes getterDef.Signature
                SignatureHandle = if getterDef.Signature.IsNil then None else Some getterDef.Signature
                FirstParameterRowId = None } ]

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        assertTableStreamMatches metadataDelta

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])
        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, methodRows.Head.RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows.Head.RowId, EditAndContinueOperation.AddParameter) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, methodRows.Head.RowId)
               (TableIndex.Param, parameterRows.Head.RowId) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, methodRows[0].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, methodRows[1].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows[0].RowId, EditAndContinueOperation.AddParameter)
               (TableIndex.Param, parameterRows[1].RowId, EditAndContinueOperation.AddParameter) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, methodRows[0].RowId)
               (TableIndex.MethodDef, methodRows[1].RowId)
               (TableIndex.Param, parameterRows[0].RowId)
               (TableIndex.Param, parameterRows[1].RowId) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap

    [<Fact>]
    let ``closure multi-generation deltas preserve EncLog ordering`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, 1, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, 2, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, 1, EditAndContinueOperation.AddParameter)
               (TableIndex.Param, 2, EditAndContinueOperation.AddParameter) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, 1)
               (TableIndex.MethodDef, 2)
               (TableIndex.Param, 1)
               (TableIndex.Param, 2) |]

        let assertDelta (delta: DeltaWriter.MetadataDelta) =
            Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, delta.EncLog)
            Assert.Equal<(TableIndex * int)[]>(expectedEncMap, delta.EncMap)
            assertTableStreamMatches delta
            assertTableCountsMatch delta.Metadata delta.TableRowCounts
            assertBitMasksMatch delta.Metadata delta.TableBitMasks
            assertEncLogMatches delta.Metadata delta.EncLog
            assertEncMapMatches delta.Metadata delta.EncMap

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
                updates
                MetadataHeapOffsets.Zero
                (getRowCounts metadataReader)

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])

        let expectedEncLog: (TableIndex * int * EditAndContinueOperation)[] =
            [| (TableIndex.Module, 1, EditAndContinueOperation.Default)
               (TableIndex.MethodDef, methodRows[0].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.MethodDef, methodRows[1].RowId, EditAndContinueOperation.AddMethod)
               (TableIndex.Param, parameterRows[0].RowId, EditAndContinueOperation.AddParameter) |]

        let expectedEncMap: (TableIndex * int)[] =
            [| (TableIndex.Module, 1)
               (TableIndex.MethodDef, methodRows[0].RowId)
               (TableIndex.MethodDef, methodRows[1].RowId)
               (TableIndex.Param, parameterRows[0].RowId) |]

        Assert.Equal<(TableIndex * int * EditAndContinueOperation)[]>(expectedEncLog, metadataDelta.EncLog)
        Assert.Equal<(TableIndex * int)[]>(expectedEncMap, metadataDelta.EncMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.Metadata metadataDelta.TableBitMasks
        assertEncLogMatches metadataDelta.Metadata metadataDelta.EncLog
        assertEncMapMatches metadataDelta.Metadata metadataDelta.EncMap
