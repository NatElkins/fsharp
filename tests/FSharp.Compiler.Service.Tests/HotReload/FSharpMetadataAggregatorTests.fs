namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Xunit
open FSharp.Compiler.HotReload
open FSharp.Compiler.CodeGen
open FSharp.Compiler.Service.Tests.HotReload.MetadataDeltaTestHelpers

module FSharpMetadataAggregatorTests =
    module DeltaWriter = FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

    let private tryGetUtf8String (reader: MetadataReader) (handle: StringHandle) =
        if handle.IsNil then
            None
        else
            try
                Some(reader.GetString handle)
            with
            | :? BadImageFormatException
            | :? ArgumentOutOfRangeException ->
                None

    let private getBaselineMethodName (reader: MetadataReader) (handle: MethodDefinitionHandle) =
        let methodDef = reader.GetMethodDefinition handle
        reader.GetString methodDef.Name

    let private getBaselinePropertyName (reader: MetadataReader) (handle: PropertyDefinitionHandle) =
        let propertyDef = reader.GetPropertyDefinition handle
        reader.GetString propertyDef.Name

    let private getBaselineEventName (reader: MetadataReader) (handle: EventDefinitionHandle) =
        let eventDef = reader.GetEventDefinition handle
        reader.GetString eventDef.Name

    let private getMethodNameWithFallback
        (aggregator: FSharpMetadataAggregator option)
        (baselineReader: MetadataReader)
        (reader: MetadataReader)
        (handle: MethodDefinitionHandle)
        =
        if obj.ReferenceEquals(reader, baselineReader) then
            getBaselineMethodName reader handle
        else
            let methodDef = reader.GetMethodDefinition handle

            match tryGetUtf8String reader methodDef.Name with
            | Some value -> value
            | None ->
                match aggregator with
                | Some agg ->
                    let struct (_, baselineHandle) = agg.TranslateMethodDefinitionHandle handle
                    getBaselineMethodName baselineReader baselineHandle
                | None ->
                    raise (InvalidOperationException "Unable to resolve method name without aggregator context.")

    let private methodNameForReader
        (aggregator: FSharpMetadataAggregator option)
        (baselineReader: MetadataReader)
        (reader: MetadataReader)
        (handle: MethodDefinitionHandle)
        =
        let aggregatorOpt =
            match aggregator with
            | Some _ when obj.ReferenceEquals(reader, baselineReader) -> None
            | _ -> aggregator

        getMethodNameWithFallback aggregatorOpt baselineReader reader handle

    let private propertyNameForReader
        (aggregator: FSharpMetadataAggregator option)
        (baselineReader: MetadataReader)
        (reader: MetadataReader)
        (handle: PropertyDefinitionHandle)
        =
        let aggregatorOpt =
            match aggregator with
            | Some _ when obj.ReferenceEquals(reader, baselineReader) -> None
            | _ -> aggregator

        if obj.ReferenceEquals(reader, baselineReader) then
            getBaselinePropertyName reader handle
        else
            let propertyDef = reader.GetPropertyDefinition handle
            match tryGetUtf8String reader propertyDef.Name with
            | Some value -> value
            | None ->
                match aggregatorOpt with
                | Some agg ->
                    let struct (_, translated) = agg.TranslatePropertyHandle handle
                    getBaselinePropertyName baselineReader translated
                | None ->
                    raise (InvalidOperationException "Unable to resolve property name without aggregator context.")

    let private eventNameForReader
        (aggregator: FSharpMetadataAggregator option)
        (baselineReader: MetadataReader)
        (reader: MetadataReader)
        (handle: EventDefinitionHandle)
        =
        let aggregatorOpt =
            match aggregator with
            | Some _ when obj.ReferenceEquals(reader, baselineReader) -> None
            | _ -> aggregator

        if obj.ReferenceEquals(reader, baselineReader) then
            getBaselineEventName reader handle
        else
            let eventDef = reader.GetEventDefinition handle
            match tryGetUtf8String reader eventDef.Name with
            | Some value -> value
            | None ->
                match aggregatorOpt with
                | Some agg ->
                    let struct (_, translated) = agg.TranslateEventHandle handle
                    getBaselineEventName baselineReader translated
                | None ->
                    raise (InvalidOperationException "Unable to resolve event name without aggregator context.")

    let private emitPropertyDelta (messageLiteral: string option) () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts messageLiteral ()
        artifacts.BaselineBytes, artifacts.Delta

    let private emitEventDelta (messageLiteral: string option) () =
        let artifacts = MetadataDeltaTestHelpers.emitEventDeltaArtifacts messageLiteral ()
        artifacts.BaselineBytes, artifacts.Delta

    [<Fact>]
    let ``aggregator translates handles to owning generation`` () =
        let baselineBytes, delta = emitPropertyDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        let deltaProvider, deltaReader =
            let provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
            provider, provider.GetMetadataReader()
        use _provider = deltaProvider

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let deltaMethodHandle =
            deltaReader.MethodDefinitions
            |> Seq.find (fun handle ->
                let name = methodNameForReader (Some aggregator) baselineReader deltaReader handle
                name = "get_Message")

        let struct (methodGeneration, translatedMethod) =
            aggregator.TranslateMethodDefinitionHandle deltaMethodHandle

        Assert.Equal(0, methodGeneration)
        Assert.Equal(deltaMethodHandle, translatedMethod)

    [<Fact>]
    let ``aggregator translates string handles to baseline generation`` () =
        let baselineBytes, delta = emitPropertyDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let deltaMethodHandle =
            deltaReader.MethodDefinitions
            |> Seq.head

        let deltaMethodDef = deltaReader.GetMethodDefinition deltaMethodHandle
        let struct(stringGeneration, translatedString) = aggregator.TranslateStringHandle(deltaReader, deltaMethodDef.Name)

        Assert.Equal(0, stringGeneration)
        let baselineValue = baselineReader.GetString translatedString
        let deltaValue =
            defaultArg (tryGetUtf8String deltaReader deltaMethodDef.Name) baselineValue
        Assert.Equal(deltaValue, baselineValue)

    [<Fact>]
    let ``aggregator translates property signature handles to baseline generation`` () =
        let baselineBytes, delta = emitPropertyDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator = FSharpMetadataAggregator.Create([ baselineReader; deltaReader ])

        let deltaPropertyHandle =
            deltaReader.PropertyDefinitions
            |> Seq.head

        let deltaProperty = deltaReader.GetPropertyDefinition deltaPropertyHandle
        let struct (generation, translatedHandle) = aggregator.TranslateBlobHandle(deltaReader, deltaProperty.Signature)

        Assert.Equal(0, generation)

        let baselinePropertyHandle =
            baselineReader.PropertyDefinitions
            |> Seq.find (fun handle ->
                let propertyDef = baselineReader.GetPropertyDefinition handle
                baselineReader.GetString(propertyDef.Name) = "Message")

        let baselinePropertyDef = baselineReader.GetPropertyDefinition baselinePropertyHandle
        Assert.Equal(baselinePropertyDef.Signature, translatedHandle)

        let baselineBytes = baselineReader.GetBlobBytes baselinePropertyDef.Signature
        let translatedBytes = baselineReader.GetBlobBytes translatedHandle
        Assert.Equal<byte>(baselineBytes, translatedBytes)

    [<Fact>]
    let ``aggregator translates method signature handles to baseline generation`` () =
        let baselineBytes, delta = emitPropertyDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator = FSharpMetadataAggregator.Create([ baselineReader; deltaReader ])

        let deltaMethodHandle =
            deltaReader.MethodDefinitions
            |> Seq.find (fun handle ->
                let methodDef = deltaReader.GetMethodDefinition handle
                let name = methodNameForReader (Some aggregator) baselineReader deltaReader handle
                name = "get_Message")

        let deltaMethodDef = deltaReader.GetMethodDefinition deltaMethodHandle
        let struct (generation, translatedHandle) = aggregator.TranslateBlobHandle(deltaReader, deltaMethodDef.Signature)

        Assert.Equal(0, generation)

        let baselineMethodHandle =
            MetadataDeltaTestHelpers.findMethodHandle baselineReader "Sample.PropertyHost" "get_Message"

        let baselineMethodDef = baselineReader.GetMethodDefinition baselineMethodHandle
        Assert.Equal(baselineMethodDef.Signature, translatedHandle)

        let baselineBytes = baselineReader.GetBlobBytes baselineMethodDef.Signature
        let translatedBytes = baselineReader.GetBlobBytes translatedHandle
        Assert.Equal<byte>(baselineBytes, translatedBytes)

    [<Fact>]
    let ``aggregator translates method signature handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()
        let baselineMethodHandle =
            MetadataDeltaTestHelpers.findMethodHandle baselineReader "Sample.PropertyHost" "get_Message"
        let baselineMethodDef = baselineReader.GetMethodDefinition baselineMethodHandle

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let deltaMethodHandle =
            deltaReader2.MethodDefinitions
            |> Seq.find (fun handle ->
                let methodDef = deltaReader2.GetMethodDefinition handle
                let name = methodNameForReader (Some aggregator) baselineReader deltaReader2 handle
                name = "get_Message")

        let deltaMethodDef = deltaReader2.GetMethodDefinition deltaMethodHandle
        let struct (generation, translatedHandle) = aggregator.TranslateBlobHandle(deltaReader2, deltaMethodDef.Signature)

        Assert.Equal(0, generation)
        Assert.Equal(baselineMethodDef.Signature, translatedHandle)

    [<Fact>]
    let ``aggregator translates property signature handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findProperty (reader: MetadataReader) aggregatorOpt =
            reader.PropertyDefinitions
            |> Seq.find (fun handle ->
                propertyNameForReader aggregatorOpt baselineReader reader handle = "Message")

        let baselinePropertyHandle = findProperty baselineReader None
        let baselineProperty = baselineReader.GetPropertyDefinition baselinePropertyHandle

        let deltaPropertyHandle = findProperty deltaReader2 (Some aggregator)
        let deltaProperty = deltaReader2.GetPropertyDefinition deltaPropertyHandle

        let struct (generation, translatedHandle) =
            aggregator.TranslateBlobHandle(deltaReader2, deltaProperty.Signature)

        Assert.Equal(0, generation)
        Assert.Equal(baselineProperty.Signature, translatedHandle)

    [<Fact>]
    let ``aggregator translates local signature handles to baseline generation`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureDeltaArtifacts None ()
        let baselineBytes = artifacts.BaselineBytes
        let delta = artifacts.Delta

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()
        let baselineMethodHandle =
            MetadataDeltaTestHelpers.findMethodHandle baselineReader "Sample.LocalSignatureHost" "FormatMessage"
        let baselineMethodDef = baselineReader.GetMethodDefinition baselineMethodHandle
        let baselineBody = peReader.GetMethodBody(baselineMethodDef.RelativeVirtualAddress)
        let baselineLocalSignatureHandle = baselineBody.LocalSignature
        Assert.False(baselineLocalSignatureHandle.IsNil)
        let baselineLocalSignature = baselineReader.GetStandaloneSignature baselineLocalSignatureHandle

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator = FSharpMetadataAggregator.Create([ baselineReader; deltaReader ])

        let deltaSignatureHandle =
            let count = deltaReader.GetTableRowCount(TableIndex.StandAloneSig)
            Assert.True(count > 0)
            MetadataTokens.StandaloneSignatureHandle 1
        let deltaSignature = deltaReader.GetStandaloneSignature deltaSignatureHandle

        let struct (generation, translatedHandle) =
            aggregator.TranslateBlobHandle(deltaReader, deltaSignature.Signature)

        Assert.Equal(0, generation)
        Assert.Equal(baselineLocalSignature.Signature, translatedHandle)

    [<Fact>]
    let ``aggregator translates local signature handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitLocalSignatureMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()
        let baselineMethodHandle =
            MetadataDeltaTestHelpers.findMethodHandle baselineReader "Sample.LocalSignatureHost" "FormatMessage"
        let baselineMethodDef = baselineReader.GetMethodDefinition baselineMethodHandle
        let baselineBody = peReader.GetMethodBody(baselineMethodDef.RelativeVirtualAddress)
        let baselineLocalSignatureHandle = baselineBody.LocalSignature
        let baselineLocalSignature = baselineReader.GetStandaloneSignature baselineLocalSignatureHandle

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let deltaSignatureHandle =
            let count = deltaReader2.GetTableRowCount(TableIndex.StandAloneSig)
            Assert.True(count > 0)
            MetadataTokens.StandaloneSignatureHandle 1
        let deltaSignature = deltaReader2.GetStandaloneSignature deltaSignatureHandle

        let struct (generation, translatedHandle) =
            aggregator.TranslateBlobHandle(deltaReader2, deltaSignature.Signature)

        Assert.Equal(0, generation)
        Assert.Equal(baselineLocalSignature.Signature, translatedHandle)

    [<Fact>]
    let ``aggregator translates event method handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findAdd (reader: MetadataReader) =
            reader.MethodDefinitions
            |> Seq.find (fun handle ->
                let name = methodNameForReader (Some aggregator) baselineReader reader handle
                name = "add_OnChanged")

        let deltaAddHandle = findAdd deltaReader2
        let struct (methodGeneration, translatedHandle) = aggregator.TranslateMethodDefinitionHandle deltaAddHandle
        Assert.Equal(0, methodGeneration)
        let baselineAddHandle = findAdd baselineReader
        Assert.Equal(baselineAddHandle, translatedHandle)

    [<Fact>]
    let ``aggregator translates event name string handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let baselineEventHandle =
            baselineReader.EventDefinitions
            |> Seq.find (fun handle ->
                eventNameForReader None baselineReader baselineReader handle = "OnChanged")

        let deltaEventHandle =
            deltaReader2.EventDefinitions
            |> Seq.find (fun handle ->
                eventNameForReader (Some aggregator) baselineReader deltaReader2 handle = "OnChanged")

        let deltaEventDef = deltaReader2.GetEventDefinition deltaEventHandle
        let struct (generation, translatedHandle) =
            aggregator.TranslateStringHandle(deltaReader2, deltaEventDef.Name)

        Assert.Equal(0, generation)
        let baselineEventDef = baselineReader.GetEventDefinition baselineEventHandle
        Assert.Equal(baselineEventDef.Name, translatedHandle)

    [<Fact>]
    let ``aggregator translates string handles across multiple generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let delta2MethodHandle =
            deltaReader2.MethodDefinitions
            |> Seq.head

        let delta2MethodDef = deltaReader2.GetMethodDefinition delta2MethodHandle
        let struct (stringGeneration, translatedHandle) = aggregator.TranslateStringHandle(deltaReader2, delta2MethodDef.Name)

        Assert.Equal(0, stringGeneration)
        let baselineValue = baselineReader.GetString translatedHandle
        let deltaValue =
            defaultArg (tryGetUtf8String deltaReader2 delta2MethodDef.Name) baselineValue
        Assert.Equal(deltaValue, baselineValue)

    [<Fact>]
    let ``aggregator translates parameter handles to baseline generation`` () =
        let baselineBytes, delta = emitEventDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let findMethod (reader: MetadataReader) name =
            reader.MethodDefinitions
            |> Seq.find (fun handle ->
                let agg = Some aggregator
                let methodName = methodNameForReader agg baselineReader reader handle
                methodName = name)

        let firstParameter (reader: MetadataReader) (methodHandle: MethodDefinitionHandle) =
            let methodDef = reader.GetMethodDefinition methodHandle
            methodDef.GetParameters()
            |> Seq.tryFind (fun parameterHandle ->
                if parameterHandle.IsNil then
                    false
                else
                    let parameter = reader.GetParameter parameterHandle
                    int parameter.SequenceNumber > 0)
                |> Option.defaultWith (fun () ->
                    let agg = Some aggregator
                    let name = methodNameForReader agg baselineReader reader methodHandle
                    failwithf "Method %s has no value parameters" name)

        let baselineAdd = findMethod baselineReader "add_OnChanged"
        let deltaAdd = findMethod deltaReader "add_OnChanged"

        let deltaParamHandle = firstParameter deltaReader deltaAdd
        let struct (generation, translatedHandle) = aggregator.TranslateParameterHandle deltaParamHandle

        Assert.Equal(0, generation)
        let baselineParamHandle = firstParameter baselineReader baselineAdd
        Assert.Equal(baselineParamHandle, translatedHandle)

    [<Fact>]
    let ``aggregator translates parameter name string handles`` () =
        let baselineBytes, delta = emitEventDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let findMethod (reader: MetadataReader) name =
            reader.MethodDefinitions
            |> Seq.find (fun handle ->
                let methodName = methodNameForReader (Some aggregator) baselineReader reader handle
                methodName = name)

        let firstParameter (reader: MetadataReader) (methodHandle: MethodDefinitionHandle) =
            let methodDef = reader.GetMethodDefinition methodHandle
            methodDef.GetParameters()
            |> Seq.tryFind (fun parameterHandle ->
                if parameterHandle.IsNil then
                    false
                else
                    let parameter = reader.GetParameter parameterHandle
                    int parameter.SequenceNumber > 0)
                |> Option.defaultWith (fun () ->
                    let name = methodNameForReader (Some aggregator) baselineReader reader methodHandle
                    failwithf "Method %s has no value parameters" name)

        let baselineAdd = findMethod baselineReader "add_OnChanged"
        let deltaAdd = findMethod deltaReader "add_OnChanged"

        let baselineParamHandle = firstParameter baselineReader baselineAdd
        let deltaParamHandle = firstParameter deltaReader deltaAdd

        let baselineParam = baselineReader.GetParameter baselineParamHandle
        let deltaParam = deltaReader.GetParameter deltaParamHandle

        let struct (stringGeneration, translatedHandle) = aggregator.TranslateStringHandle(deltaReader, deltaParam.Name)

        Assert.Equal(0, stringGeneration)
        Assert.Equal(
            baselineReader.GetString baselineParam.Name,
            baselineReader.GetString translatedHandle)

    [<Fact>]
    let ``aggregator translates property name string handles to baseline generation`` () =
        let baselineBytes, delta = emitPropertyDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let findProperty (reader: MetadataReader) =
            reader.PropertyDefinitions
            |> Seq.find (fun handle ->
                let name = propertyNameForReader (Some aggregator) baselineReader reader handle
                name = "Message")

        let baselineProperty = findProperty baselineReader
        let deltaProperty = findProperty deltaReader

        let baselineDef = baselineReader.GetPropertyDefinition baselineProperty
        let deltaDef = deltaReader.GetPropertyDefinition deltaProperty

        let struct (generation, translatedHandle) = aggregator.TranslateStringHandle(deltaReader, deltaDef.Name)

        Assert.Equal(0, generation)
        Assert.Equal(
            baselineReader.GetString baselineDef.Name,
            baselineReader.GetString translatedHandle)

    [<Fact>]
    let ``aggregator translates event name string handles to baseline generation`` () =
        let baselineBytes, delta = emitEventDelta None ()

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(delta.Metadata))
        let deltaReader = deltaProvider.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader ])

        let findEvent (reader: MetadataReader) =
            reader.EventDefinitions
            |> Seq.find (fun handle ->
                let name = eventNameForReader (Some aggregator) baselineReader reader handle
                name = "OnChanged")

        let baselineEvent = findEvent baselineReader
        let deltaEvent = findEvent deltaReader

        let baselineDef = baselineReader.GetEventDefinition baselineEvent
        let deltaDef = deltaReader.GetEventDefinition deltaEvent

        let struct (generation, translatedHandle) = aggregator.TranslateStringHandle(deltaReader, deltaDef.Name)

        Assert.Equal(0, generation)
        Assert.Equal(
            baselineReader.GetString baselineDef.Name,
            baselineReader.GetString translatedHandle)

    [<Fact>]
    let ``aggregator translates property handles across multiple generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findProperty (reader: MetadataReader) =
            reader.PropertyDefinitions
            |> Seq.find (fun handle ->
                let name = propertyNameForReader (Some aggregator) baselineReader reader handle
                name = "Message")

        let deltaProperty = findProperty deltaReader2
        let struct (generation, translated) = aggregator.TranslatePropertyHandle deltaProperty
        Assert.Equal(0, generation)
        let baselineProperty = findProperty baselineReader
        Assert.Equal(baselineProperty, translated)

    [<Fact>]
    let ``aggregator translates parameter handles across multiple generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findMethod (reader: MetadataReader) name =
            reader.MethodDefinitions
            |> Seq.find (fun handle ->
                let methodName = methodNameForReader (Some aggregator) baselineReader reader handle
                methodName = name)

        let firstParameter (reader: MetadataReader) (methodHandle: MethodDefinitionHandle) =
            let methodDef = reader.GetMethodDefinition methodHandle
            methodDef.GetParameters()
            |> Seq.tryFind (fun parameterHandle ->
                if parameterHandle.IsNil then
                    false
                else
                    let parameter = reader.GetParameter parameterHandle
                    int parameter.SequenceNumber > 0)
                |> Option.defaultWith (fun () ->
                    let name = methodNameForReader (Some aggregator) baselineReader reader methodHandle
                    failwithf "Method %s has no value parameters" name)

        let baselineAdd = findMethod baselineReader "add_OnChanged"
        let delta2Add = findMethod deltaReader2 "add_OnChanged"

        let deltaParamHandle = firstParameter deltaReader2 delta2Add
        let struct (generation, translatedHandle) = aggregator.TranslateParameterHandle deltaParamHandle

        Assert.Equal(0, generation)
        let baselineParamHandle = firstParameter baselineReader baselineAdd
        Assert.Equal(baselineParamHandle, translatedHandle)

    [<Fact>]
    let ``aggregator translates closure method handles across generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitClosureMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 =
            MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 =
            MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findMethod (reader: MetadataReader) name =
            reader.MethodDefinitions
            |> Seq.find (fun handle ->
                let methodName = methodNameForReader (Some aggregator) baselineReader reader handle
                methodName = name)

        let assertTranslated name =
            let deltaHandle = findMethod deltaReader2 name
            let struct (generation, translated) = aggregator.TranslateMethodDefinitionHandle deltaHandle
            Assert.Equal(0, generation)
            let baselineHandle = findMethod baselineReader name
            Assert.Equal(baselineHandle, translated)

        assertTranslated "InvokeOuter"
        assertTranslated "Invoke@40-1"

    [<Fact>]
    let ``aggregator translates event handles across multiple generations`` () =
        let artifacts = MetadataDeltaTestHelpers.emitEventMultiGenerationArtifacts ()
        let baselineBytes = artifacts.BaselineBytes
        let deltaGen1 = artifacts.Generation1
        let deltaGen2 = artifacts.Generation2

        use peReader = new PEReader(new MemoryStream(baselineBytes, writable = false))
        let baselineReader = peReader.GetMetadataReader()

        use deltaProvider1 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen1.Metadata))
        let deltaReader1 = deltaProvider1.GetMetadataReader()

        use deltaProvider2 = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(deltaGen2.Metadata))
        let deltaReader2 = deltaProvider2.GetMetadataReader()

        let aggregator =
            FSharpMetadataAggregator.Create(
                [ baselineReader
                  deltaReader1
                  deltaReader2 ])

        let findEvent (reader: MetadataReader) =
            reader.EventDefinitions
            |> Seq.find (fun handle ->
                let name = eventNameForReader (Some aggregator) baselineReader reader handle
                name = "OnChanged")

        let deltaEvent = findEvent deltaReader2
        let struct (generation, translated) = aggregator.TranslateEventHandle deltaEvent
        Assert.Equal(0, generation)
        let baselineEvent = findEvent baselineReader
        Assert.Equal(baselineEvent, translated)
