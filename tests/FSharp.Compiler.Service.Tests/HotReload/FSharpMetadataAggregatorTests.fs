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

    let private emitPropertyDelta () =
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts ()
        artifacts.BaselineBytes, artifacts.Delta

    [<Fact>]
    let ``aggregator translates handles to owning generation`` () =
        let baselineBytes, delta = emitPropertyDelta ()

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
            |> Seq.head

        let struct (methodGeneration, translatedMethod) =
            aggregator.TranslateMethodDefinitionHandle deltaMethodHandle

        Assert.Equal(0, methodGeneration)
        Assert.Equal(deltaMethodHandle, translatedMethod)

    [<Fact>]
    let ``aggregator translates string handles to baseline generation`` () =
        let baselineBytes, delta = emitPropertyDelta ()

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
