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
                let methodDef = deltaReader.GetMethodDefinition(handle)
                let name = deltaReader.GetString(methodDef.Name)
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
        let struct(stringGeneration, translatedString) = aggregator.TranslateStringHandle deltaMethodDef.Name

        Assert.Equal(0, stringGeneration)
        let baselineValue = baselineReader.GetString translatedString
        let deltaValue = deltaReader.GetString deltaMethodDef.Name
        Assert.Equal(deltaValue, baselineValue)

    [<Fact>]
    let ``aggregator translates event method handles across generations`` () =
        let baselineBytes, deltaGen1 = emitEventDelta (Some "event-one") ()
        let _, deltaGen2 = emitEventDelta (Some "event-two") ()

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
            |> Seq.find (fun handle -> reader.GetString(reader.GetMethodDefinition(handle).Name) = "add_OnChanged")

        let deltaAddHandle = findAdd deltaReader2
        let struct (methodGeneration, translatedHandle) = aggregator.TranslateMethodDefinitionHandle deltaAddHandle
        Assert.Equal(0, methodGeneration)
        let baselineAddHandle = findAdd baselineReader
        Assert.Equal(baselineAddHandle, translatedHandle)

    [<Fact>]
    let ``aggregator translates string handles across multiple generations`` () =
        let baselineBytes, deltaGen1 = emitPropertyDelta (Some "generation-one") ()
        let _, deltaGen2 = emitPropertyDelta (Some "generation-two") ()

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
        let struct (stringGeneration, translatedHandle) = aggregator.TranslateStringHandle delta2MethodDef.Name

        Assert.Equal(0, stringGeneration)
        Assert.Equal(
            deltaReader2.GetString delta2MethodDef.Name,
            baselineReader.GetString translatedHandle)
