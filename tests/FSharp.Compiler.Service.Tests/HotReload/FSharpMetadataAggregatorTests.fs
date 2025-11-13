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
            |> Seq.find (fun handle -> reader.GetString(reader.GetMethodDefinition(handle).Name) = "add_OnChanged")

        let deltaAddHandle = findAdd deltaReader2
        let struct (methodGeneration, translatedHandle) = aggregator.TranslateMethodDefinitionHandle deltaAddHandle
        Assert.Equal(0, methodGeneration)
        let baselineAddHandle = findAdd baselineReader
        Assert.Equal(baselineAddHandle, translatedHandle)

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
        let struct (stringGeneration, translatedHandle) = aggregator.TranslateStringHandle delta2MethodDef.Name

        Assert.Equal(0, stringGeneration)
        Assert.Equal(
            deltaReader2.GetString delta2MethodDef.Name,
            baselineReader.GetString translatedHandle)

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
            |> Seq.find (fun handle -> reader.GetString(reader.GetMethodDefinition(handle).Name) = name)

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
                let name = reader.GetString(methodDef.Name)
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
            |> Seq.find (fun handle -> reader.GetString(reader.GetMethodDefinition(handle).Name) = name)

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
                let name = reader.GetString(methodDef.Name)
                failwithf "Method %s has no value parameters" name)

        let baselineAdd = findMethod baselineReader "add_OnChanged"
        let deltaAdd = findMethod deltaReader "add_OnChanged"

        let baselineParamHandle = firstParameter baselineReader baselineAdd
        let deltaParamHandle = firstParameter deltaReader deltaAdd

        let baselineParam = baselineReader.GetParameter baselineParamHandle
        let deltaParam = deltaReader.GetParameter deltaParamHandle

        let struct (stringGeneration, translatedHandle) = aggregator.TranslateStringHandle deltaParam.Name

        Assert.Equal(0, stringGeneration)
        Assert.Equal(
            baselineReader.GetString baselineParam.Name,
            baselineReader.GetString translatedHandle)

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
            |> Seq.find (fun handle -> reader.GetString(reader.GetMethodDefinition(handle).Name) = name)

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
                let name = reader.GetString(methodDef.Name)
                failwithf "Method %s has no value parameters" name)

        let baselineAdd = findMethod baselineReader "add_OnChanged"
        let delta2Add = findMethod deltaReader2 "add_OnChanged"

        let deltaParamHandle = firstParameter deltaReader2 delta2Add
        let struct (generation, translatedHandle) = aggregator.TranslateParameterHandle deltaParamHandle

        Assert.Equal(0, generation)
        let baselineParamHandle = firstParameter baselineReader baselineAdd
        Assert.Equal(baselineParamHandle, translatedHandle)
