namespace FSharp.Compiler.ComponentTests.HotReload

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TypedTreeDiff

module internal TestHelpers =

    module ILWriter = FSharp.Compiler.AbstractIL.ILBinaryWriter

    let defaultWriterOptionsForTests (ilg: ILGlobals) : ILWriter.options =
        let scratchDll = Path.Combine(Path.GetTempPath(), sprintf "fsharp-hotreload-test-%s.dll" (Guid.NewGuid().ToString("N")))
        { ilg = ilg
          outfile = scratchDll
          pdbfile = None
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

    let createPropertyModule (message: string) : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let stringType = ilg.typ_String
        let typeName = "Sample.PropertyDemo"
        let typeRef = mkILTyRef(ILScopeRef.Local, typeName)

        let getterBody =
            mkMethodBody (
                false,
                [],
                2,
                nonBranchingInstrsToCode [ I_ldstr message; I_ret ],
                None,
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

    let createEventModule () : ILModuleDef =
        let ilg = PrimaryAssemblyILGlobals
        let typeName = "Sample.EventDemo"
        let typeRef = mkILTyRef(ILScopeRef.Local, typeName)
        let voidType = ILType.Void
        let handlerType = ilg.typ_Object

        let methodBody = mkMethodBody(false, [], 1, nonBranchingInstrsToCode [ I_ret ], None, None)

        let addMethod =
            mkILNonGenericInstanceMethod("add_OnChanged", ILMemberAccess.Public, [ mkILParamNamed ("handler", handlerType) ], mkILReturn voidType, methodBody)
            |> fun methodDef -> methodDef.WithSpecialName.WithHideBySig(true)

        let removeMethod =
            mkILNonGenericInstanceMethod("remove_OnChanged", ILMemberAccess.Public, [ mkILParamNamed ("handler", handlerType) ], mkILReturn voidType, methodBody)
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

    let createBaselineFromModule (ilModule: ILModuleDef) : FSharpEmitBaseline * ILTokenMappings * Guid * MetadataSnapshot =
        let writerOptions = defaultWriterOptionsForTests PrimaryAssemblyILGlobals
        let assemblyBytes, _pdbBytes, tokenMappings, _ =
            ILWriter.WriteILBinaryInMemoryWithArtifacts(writerOptions, ilModule, id)

        use peReader = new PEReader(new MemoryStream(assemblyBytes, writable = false))
        let metadataReader = peReader.GetMetadataReader()
        let metadataSnapshot = metadataSnapshotFromReader metadataReader
        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleId =
            if moduleDef.Mvid.IsNil then Guid.NewGuid() else metadataReader.GetGuid(moduleDef.Mvid)

        let baseline = create ilModule tokenMappings metadataSnapshot moduleId None
        baseline, tokenMappings, moduleId, metadataSnapshot

    let methodKeyByName (baseline: FSharpEmitBaseline) typeName methodName =
        baseline.MethodTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.find (fun key -> key.DeclaringType = typeName && key.Name = methodName)

    let propertyKeyByName (baseline: FSharpEmitBaseline) typeName propertyName =
        baseline.PropertyTokens
        |> Map.toSeq
        |> Seq.map fst
        |> Seq.tryFind (fun key -> key.DeclaringType = typeName && key.Name = propertyName)

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
