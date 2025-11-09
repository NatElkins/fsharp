namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
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

module ILWriter = FSharp.Compiler.AbstractIL.ILBinaryWriter
module ILPdbWriter = FSharp.Compiler.AbstractIL.ILPdbWriter
module DeltaWriter = FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

module private MetadataWriterTestHelpers =
    let private mscorlibToken =
        PublicKeyToken [|
            0xb7uy
            0x7auy
            0x5cuy
            0x56uy
            0x19uy
            0x34uy
            0xe0uy
            0x89uy
        |]

    let private fsharpCoreToken =
        PublicKeyToken [|
            0xb0uy
            0x3fuy
            0x5fuy
            0x7fuy
            0x11uy
            0xd5uy
            0x0auy
            0x3auy
        |]

    let private mscorlibRef =
        ILAssemblyRef.Create(
            "mscorlib",
            None,
            Some mscorlibToken,
            false,
            Some(ILVersionInfo(4us, 0us, 0us, 0us)),
            None)

    let private fsharpCoreRef =
        ILAssemblyRef.Create(
            "FSharp.Core",
            None,
            Some fsharpCoreToken,
            false,
            Some(ILVersionInfo(0us, 0us, 0us, 0us)),
            None)

    let private testIlGlobals =
        mkILGlobals(ILScopeRef.Assembly mscorlibRef, [], ILScopeRef.Assembly fsharpCoreRef)

    let private defaultWriterOptions (ilg: ILGlobals) : ILWriter.options =
        { ilg = ilg
          outfile = Path.GetTempFileName()
          pdbfile = None
          portablePDB = true
          embeddedPDB = false
          embedAllSource = false
          embedSourceList = []
          allGivenSources = []
          sourceLink = ""
          checksumAlgorithm = ILPdbWriter.HashAlgorithm.Sha256
          signer = None
          emitTailcalls = false
          deterministic = true
          dumpDebugInfo = false
          referenceAssemblyOnly = false
          referenceAssemblyAttribOpt = None
          referenceAssemblySignatureHash = None
          pathMap = PathMap.empty }

    let createAssemblyBytes (moduleDef: ILModuleDef) =
        let options = defaultWriterOptions testIlGlobals
        ILWriter.WriteILBinaryInMemoryWithArtifacts(options, moduleDef, id)

    let ilGlobals = testIlGlobals

    let methodKey (typeName: string) name returnType =
        { DeclaringType = typeName
          Name = name
          GenericArity = 0
          ParameterTypes = []
          ReturnType = returnType }

module FSharpDeltaMetadataWriterTests =
    open MetadataWriterTestHelpers

    let private createPropertyModule () =
        let ilg = ilGlobals
        let stringType = ilg.typ_String
        let typeName = "Sample.PropertyHost"

        let getterBody =
            mkMethodBody(
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_ldstr "delta"; I_ret ],
                None,
                None)

        let getter =
            mkILNonGenericInstanceMethod(
                "get_Message",
                ILMemberAccess.Public,
                [],
                mkILReturn stringType,
                getterBody)
            |> fun def -> def.WithSpecialName.WithHideBySig(true)

        let propertyDef =
            ILPropertyDef(
                "Message",
                PropertyAttributes.None,
                None,
                Some(mkILMethRef(mkILTyRef(ILScopeRef.Local, typeName), ILCallingConv.Instance, "get_Message", 0, [], stringType)),
                ILThisConvention.Instance,
                stringType,
                None,
                [],
                emptyILCustomAttrs)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    typeName,
                    ILTypeDefAccess.Public,
                    mkILMethods [ getter ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [ propertyDef ],
                    mkILEvents [],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField )

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

    let private createEventModule () =
        let ilg = ilGlobals
        let typeName = "Sample.EventHost"
        let typeRef = mkILTyRef(ILScopeRef.Local, typeName)

        let accessorBody =
            mkMethodBody(
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_ret ],
                None,
                None)

        let makeAccessor name =
            mkILNonGenericInstanceMethod(
                name,
                ILMemberAccess.Public,
                [],
                mkILReturn ILType.Void,
                accessorBody)
            |> fun methodDef -> methodDef.WithSpecialName.WithHideBySig(true)

        let addMethod = makeAccessor "add_OnChanged"
        let removeMethod = makeAccessor "remove_OnChanged"

        let eventDef =
            ILEventDef(
                Some ilg.typ_Object,
                "OnChanged",
                EventAttributes.None,
                mkILMethRef(typeRef, ILCallingConv.Instance, "add_OnChanged", 0, [], ILType.Void),
                mkILMethRef(typeRef, ILCallingConv.Instance, "remove_OnChanged", 0, [], ILType.Void),
                None,
                [],
                emptyILCustomAttrs)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    typeName,
                    ILTypeDefAccess.Public,
                    mkILMethods [ addMethod; removeMethod ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
                    mkILEvents [ eventDef ],
                    emptyILCustomAttrs,
                    ILTypeInit.BeforeField )

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

    [<Fact>]
    let ``metadata writer emits property rows`` () =
        let moduleDef = createPropertyModule ()
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
                Signature = metadataReader.GetBlobBytes getterDef.Signature
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
                Signature = metadataReader.GetBlobBytes propertyDef.Signature
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
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())
                methodDefinitionRows
                []
                propertyRows
                []
                propertyMapRows
                []
                []
                updates

        let tableCount index = metadataDelta.TableRowCounts.[ int index ]

        Assert.Equal(1, tableCount TableIndex.Property)
        Assert.Equal(1, tableCount TableIndex.PropertyMap)
        let tryOperation table =
            metadataDelta.EncLog
            |> Array.tryFind (fun (index, _, _) -> index = table)
            |> Option.map (fun (_, _, op) -> op)

        Assert.Equal(Some EditAndContinueOperation.AddProperty, tryOperation TableIndex.Property)
        Assert.Equal(Some EditAndContinueOperation.Default, tryOperation TableIndex.PropertyMap)
        Assert.True(metadataDelta.Metadata.Length > 0)
        Assert.Contains("Message", Encoding.UTF8.GetString(metadataDelta.StringHeap))

    [<Fact>]
    let ``metadata writer emits event and method semantics rows`` () =
        let moduleDef = createEventModule ()
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
                Signature = metadataReader.GetBlobBytes addDef.Signature
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
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())
                methodDefinitionRows
                []
                []
                eventRows
                []
                eventMapRows
                methodSemanticsRows
                updates

        let tableCount index = metadataDelta.TableRowCounts.[int index]
        Assert.Equal(1, tableCount TableIndex.Event)
        Assert.Equal(1, tableCount TableIndex.EventMap)
        Assert.Equal(1, tableCount TableIndex.MethodSemantics)
        Assert.Contains("OnChanged", Encoding.UTF8.GetString(metadataDelta.StringHeap))

        let tryOperation table =
            metadataDelta.EncLog
            |> Array.tryFind (fun (encTable, _, _) -> encTable = table)
            |> Option.map (fun (_, _, op) -> op)

        Assert.Equal(Some EditAndContinueOperation.AddEvent, tryOperation TableIndex.Event)
        Assert.Equal(Some EditAndContinueOperation.Default, tryOperation TableIndex.EventMap)
        Assert.Equal(Some EditAndContinueOperation.Default, tryOperation TableIndex.MethodSemantics)
