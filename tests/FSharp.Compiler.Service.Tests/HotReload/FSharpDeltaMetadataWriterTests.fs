namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Collections.Immutable
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

    let private metadataStreamNames (metadata: byte[]) =
        use stream = new MemoryStream(metadata, false)
        use reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen = true)

        let readUInt32 () = reader.ReadUInt32()
        let readUInt16 () = reader.ReadUInt16()

        let _signature = readUInt32 ()
        let _major = readUInt16 ()
        let _minor = readUInt16 ()
        let _reserved = readUInt32 ()
        let versionLength = int (readUInt32 ())
        reader.ReadBytes(versionLength) |> ignore
        while stream.Position % 4L <> 0L do
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
            while stream.Position % 4L <> 0L do
                reader.ReadByte() |> ignore
            Encoding.UTF8.GetString(buffer.ToArray())

        [ for _ in 1 .. streamCount do
              let _offset = readUInt32 ()
              let _size = readUInt32 ()
              yield readStreamName () ]

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

    let private withMetadataReader (metadata: byte[]) (action: MetadataReader -> unit) =
        use provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange metadata)
        let reader = provider.GetMetadataReader()
        action reader

    let private assertTableCountsMatch metadata (expected: int[]) =
        withMetadataReader metadata (fun reader ->
            for i = 0 to expected.Length - 1 do
                let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte i)
                let actual = reader.GetTableRowCount table
                Assert.Equal(expected.[i], actual))

    let private assertBitMasksMatch (bitMasks: TableBitMasks) (rowCounts: int[]) =
        let mutable validLow = 0
        let mutable validHigh = 0
        for tableIndex = 0 to rowCounts.Length - 1 do
            if rowCounts.[tableIndex] <> 0 then
                if tableIndex < 32 then
                    validLow <- validLow ||| (1 <<< tableIndex)
                else
                    validHigh <- validHigh ||| (1 <<< (tableIndex - 32))

        Assert.Equal(validLow, bitMasks.ValidLow)
        Assert.Equal(validHigh, bitMasks.ValidHigh)

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
        let tryOperation table =
            metadataDelta.EncLog
            |> Array.tryFind (fun (index, _, _) -> index = table)
            |> Option.map (fun (_, _, op) -> op)

        Assert.Equal(Some EditAndContinueOperation.AddProperty, tryOperation TableIndex.Property)
        // Roslyn logs the containing map row as AddProperty (not AddPropertyMap).
        Assert.Equal(Some EditAndContinueOperation.AddProperty, tryOperation TableIndex.PropertyMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        Assert.Contains("Message", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.TableBitMasks metadataDelta.TableRowCounts

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
        Assert.Contains("OnChanged", Encoding.UTF8.GetString(metadataDelta.StringHeap))
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.TableBitMasks metadataDelta.TableRowCounts

    [<Fact>]
    let ``metadata writer emits async method rows`` () =
        let artifacts = MetadataDeltaTestHelpers.emitAsyncDeltaArtifacts None ()
        let metadataDelta = artifacts.Delta

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(0, metadataDelta.TableRowCounts.[int TableIndex.Param])
        Assert.True(metadataDelta.Metadata.Length > 0)
        assertTableStreamMatches metadataDelta
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.TableBitMasks metadataDelta.TableRowCounts
        assertTableCountsMatch metadataDelta.Metadata metadataDelta.TableRowCounts
        assertBitMasksMatch metadataDelta.TableBitMasks metadataDelta.TableRowCounts

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
                (getRowCounts metadataReader)
                (getRowCounts metadataReader)

        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])

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

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.Param])

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

        Assert.Equal(2, metadataDelta.TableRowCounts.[int TableIndex.MethodDef])
        Assert.Equal(1, metadataDelta.TableRowCounts.[int TableIndex.Param])
