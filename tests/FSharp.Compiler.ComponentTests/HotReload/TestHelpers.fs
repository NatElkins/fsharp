namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TypedTreeDiff

type internal BaselineArtifacts =
    {
        Baseline: FSharpEmitBaseline
        TokenMappings: ILTokenMappings
        ModuleId: Guid
        MetadataSnapshot: MetadataSnapshot
        AssemblyPath: string
        PdbPath: string option
    }

module internal TestHelpers =

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

    module ILWriter = FSharp.Compiler.AbstractIL.ILBinaryWriter

    let defaultWriterOptionsForTests (ilg: ILGlobals) : ILWriter.options =
        let scratchDll = Path.Combine(Path.GetTempPath(), sprintf "fsharp-hotreload-test-%s.dll" (Guid.NewGuid().ToString("N")))
        let scratchPdb = Path.ChangeExtension(scratchDll, ".pdb")
        { ilg = ilg
          outfile = scratchDll
          pdbfile = Some scratchPdb
          portablePDB = true
          embeddedPDB = false
          embedAllSource = false
          embedSourceList = []
          allGivenSources = []
          sourceLink = ""
          checksumAlgorithm = HashAlgorithm.Sha256
          signer = None
          emitTailcalls = false
          deterministic = true
          dumpDebugInfo = false
          referenceAssemblyOnly = false
          referenceAssemblyAttribOpt = None
          referenceAssemblySignatureHash = None
          pathMap = PathMap.empty }

    let private collectSourceDocuments (ilModule: ILModuleDef) : ILSourceDocument list =
        let docs = HashSet<ILSourceDocument>(HashIdentity.Reference)

        let addDoc (doc: ILSourceDocument) =
            if not (isNull (box doc)) then
                docs.Add doc |> ignore

        let rec collectInstr (instr: ILInstr) =
            match instr with
            | I_seqpoint debugPoint -> addDoc debugPoint.Document
            | _ -> ()

        let collectCode (code: ILCode) =
            code.Instrs |> Array.iter collectInstr

        let collectMethod (methodDef: ILMethodDef) =
            match methodDef.Body with
            | MethodBody.IL ilBodyLazy ->
                let ilBody = ilBodyLazy.Value
                collectCode ilBody.Code
                match ilBody.DebugRange with
                | Some debugPoint -> addDoc debugPoint.Document
                | None -> ()
            | _ -> ()

        let rec collectTypeDef (typeDef: ILTypeDef) =
            typeDef.Methods.AsList() |> List.iter collectMethod
            typeDef.NestedTypes.AsList() |> List.iter collectTypeDef

        ilModule.TypeDefs.AsList() |> List.iter collectTypeDef

        docs |> Seq.toList

    let createPropertyModule (message: string) : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let stringType = ilg.typ_String
        let typeName = "Sample.PropertyDemo"
        let typeRef = mkILTyRef(ILScopeRef.Local, typeName)
        let document = ILSourceDocument.Create(None, None, None, "PropertyDemo.fs")
        let debugPoint = ILDebugPoint.Create(document, 1, 1, 1, 40)

        let getterBody =
            mkMethodBody (
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_seqpoint debugPoint; I_ldstr message; I_ret ],
                Some debugPoint,
                None)

        let getter =
            mkILNonGenericInstanceMethod(
                "get_Message",
                ILMemberAccess.Public,
                [],
                mkILReturn stringType,
                getterBody)
            |> fun methodDef -> methodDef.WithSpecialName.WithHideBySig(true)

        let propertyDef =
            ILPropertyDef(
                "Message",
                PropertyAttributes.None,
                None,
                Some(mkILMethRef(typeRef, ILCallingConv.Instance, "get_Message", 0, [], stringType)),
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

    let createPropertyHostBaselineModule () : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let typeName = "Sample.PropertyDemo"
        let document = ILSourceDocument.Create(None, None, None, "PropertyDemo.fs")
        let debugPoint = ILDebugPoint.Create(document, 1, 1, 1, 20)

        let methodBody =
            mkMethodBody(
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_seqpoint debugPoint; I_ldstr "Host baseline"; I_ret ],
                Some debugPoint,
                None)

        let methodDef =
            mkILNonGenericInstanceMethod(
                "GetBaseline",
                ILMemberAccess.Public,
                [],
                mkILReturn ilg.typ_String,
                methodBody)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    typeName,
                    ILTypeDefAccess.Public,
                    mkILMethods [ methodDef ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
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

    let createEventModule (message: string) : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let typeName = "Sample.EventDemo"
        let typeRef = mkILTyRef(ILScopeRef.Local, typeName)
        let voidType = ILType.Void
        let handlerType = ilg.typ_Object

        let document = ILSourceDocument.Create(None, None, None, "EventDemo.fs")
        let addPoint = ILDebugPoint.Create(document, 1, 1, 1, 50)
        let removePoint = ILDebugPoint.Create(document, 10, 1, 10, 50)

        let addBody =
            mkMethodBody(
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_seqpoint addPoint; I_ldstr message; AI_pop; I_ret ],
                Some addPoint,
                None)

        let removeBody =
            mkMethodBody(
                false,
                [],
                1,
                nonBranchingInstrsToCode [ I_seqpoint removePoint; I_ret ],
                Some removePoint,
                None)

        let addMethod =
            mkILNonGenericInstanceMethod(
                "add_OnChanged",
                ILMemberAccess.Public,
                [ mkILParamNamed ("handler", handlerType) ],
                mkILReturn voidType,
                addBody)
            |> fun methodDef -> methodDef.WithSpecialName.WithHideBySig(true)

        let removeMethod =
            mkILNonGenericInstanceMethod(
                "remove_OnChanged",
                ILMemberAccess.Public,
                [ mkILParamNamed ("handler", handlerType) ],
                mkILReturn voidType,
                removeBody)
            |> fun methodDef -> methodDef.WithSpecialName.WithHideBySig(true)

        let eventDef =
            ILEventDef(
                Some handlerType,
                "OnChanged",
                EventAttributes.None,
                mkILMethRef(typeRef, ILCallingConv.Instance, "add_OnChanged", 0, [ handlerType ], voidType),
                mkILMethRef(typeRef, ILCallingConv.Instance, "remove_OnChanged", 0, [ handlerType ], voidType),
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

    let createEventHostBaselineModule () : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let typeName = "Sample.EventDemo"
        let document = ILSourceDocument.Create(None, None, None, "EventDemo.fs")
        let debugPoint = ILDebugPoint.Create(document, 1, 1, 1, 30)

        let invokeBody =
            mkMethodBody(
                false,
                [],
                1,
                nonBranchingInstrsToCode [ I_seqpoint debugPoint; I_ldstr "Host"; AI_pop; I_ret ],
                Some debugPoint,
                None)

        let invokeMethod =
            mkILNonGenericInstanceMethod(
                "Invoke",
                ILMemberAccess.Public,
                [ mkILParamNamed("handler", ilg.typ_Object) ],
                mkILReturn ILType.Void,
                invokeBody)

        let typeDef =
            mkILSimpleClass
                ilg
                (
                    typeName,
                    ILTypeDefAccess.Public,
                    mkILMethods [ invokeMethod ],
                    mkILFields [],
                    emptyILTypeDefs,
                    mkILProperties [],
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

    let private computePdbRowCounts (reader: MetadataReader) : ImmutableArray<int> =
        let counts = Array.zeroCreate<int> MetadataTokens.TableCount

        let inline setCount (index: TableIndex) (value: int) =
            counts[int index] <- value

        setCount TableIndex.Document reader.Documents.Count
        setCount TableIndex.MethodDebugInformation reader.MethodDebugInformation.Count
        setCount TableIndex.LocalScope reader.LocalScopes.Count
        setCount TableIndex.LocalVariable reader.LocalVariables.Count
        setCount TableIndex.LocalConstant reader.LocalConstants.Count
        setCount TableIndex.ImportScope reader.ImportScopes.Count
        setCount TableIndex.CustomDebugInformation reader.CustomDebugInformation.Count

        ImmutableArray.CreateRange counts

    let private createPortablePdbSnapshot (pdbBytes: byte[]) : PortablePdbSnapshot =
        use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
        let reader = provider.GetMetadataReader()
        let rowCounts = computePdbRowCounts reader
        let entryPointHandle = reader.DebugMetadataHeader.EntryPoint

        let entryPointToken =
            if entryPointHandle.IsNil then
                None
            else
                let entityHandle: EntityHandle = MethodDefinitionHandle.op_Implicit entryPointHandle
                Some(MetadataTokens.GetToken entityHandle)

        { Bytes = Array.copy pdbBytes
          TableRowCounts = rowCounts
          EntryPointToken = entryPointToken }

    let createBaselineFromModule (ilModule: ILModuleDef) : BaselineArtifacts =
        let documents = collectSourceDocuments ilModule
        let writerOptions =
            { defaultWriterOptionsForTests testIlGlobals with
                allGivenSources = documents }
        let assemblyBytes, pdbBytesOpt, tokenMappings, _ =
            ILWriter.WriteILBinaryInMemoryWithArtifacts(writerOptions, ilModule, id)

        File.WriteAllBytes(writerOptions.outfile, assemblyBytes)

        let pdbPath =
            match writerOptions.pdbfile, pdbBytesOpt with
            | Some path, Some bytes ->
                File.WriteAllBytes(path, bytes)
                Some path
            | _ -> None

        use peReader = new PEReader(new MemoryStream(assemblyBytes, writable = false))
        let metadataReader = peReader.GetMetadataReader()
        let metadataSnapshot = metadataSnapshotFromReader metadataReader
        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleId =
            if moduleDef.Mvid.IsNil then Guid.NewGuid() else metadataReader.GetGuid(moduleDef.Mvid)

        let portablePdbSnapshot = pdbBytesOpt |> Option.map createPortablePdbSnapshot

        let baseline = create ilModule tokenMappings metadataSnapshot moduleId portablePdbSnapshot

        { Baseline = baseline
          TokenMappings = tokenMappings
          ModuleId = moduleId
          MetadataSnapshot = metadataSnapshot
          AssemblyPath = writerOptions.outfile
          PdbPath = pdbPath }

    let methodKeyByName (baseline: FSharpEmitBaseline) typeName methodName =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.DeclaringType = typeName && key.Name = methodName)

    let methodKey
        (typeName: string)
        (methodName: string)
        (parameterTypes: ILType list)
        (returnType: ILType)
        : MethodDefinitionKey =
        { DeclaringType = typeName
          Name = methodName
          GenericArity = 0
          ParameterTypes = parameterTypes
          ReturnType = returnType }

    let propertyKeyByName (baseline: FSharpEmitBaseline) typeName propertyName =
        baseline.PropertyTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.tryFind (fun key -> key.DeclaringType = typeName && key.Name = propertyName)

    let assertBaselineDocument (pdbPath: string option) (expectedName: string) : unit =
        match pdbPath with
        | None -> failwithf "Baseline PDB path missing (expected document '%s')." expectedName
        | Some path ->
            let bytes = File.ReadAllBytes path |> ImmutableArray.CreateRange
            use provider = MetadataReaderProvider.FromPortablePdbImage(bytes)
            let reader = provider.GetMetadataReader()
            let hasDocument =
                reader.Documents
                |> Seq.exists (fun handle ->
                    let document = reader.GetDocument handle
                    reader.GetString(document.Name) = expectedName)
            if not hasDocument then
                failwithf "Baseline PDB '%s' did not contain document '%s'." path expectedName

    let mkAccessorUpdate (typeName: string) (memberKind: SymbolMemberKind) (methodKey: MethodDefinitionKey) =
        let logicalName =
            match memberKind with
            | SymbolMemberKind.PropertyGet name
            | SymbolMemberKind.PropertySet name
            | SymbolMemberKind.EventAdd name
            | SymbolMemberKind.EventRemove name
            | SymbolMemberKind.EventInvoke name -> name
            | SymbolMemberKind.Method -> methodKey.Name

        let symbol =
            { Path = typeName.Split('.') |> Array.toList
              LogicalName = logicalName
              Stamp = 0L
              Kind = SymbolKind.Value
              MemberKind = Some memberKind
              IsSynthesized = false }

        { AccessorUpdate.Symbol = symbol
          ContainingType = typeName
          MemberKind = memberKind
          Method = Some methodKey }
