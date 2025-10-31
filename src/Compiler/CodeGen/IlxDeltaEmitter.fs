module internal FSharp.Compiler.IlxDeltaEmitter

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILDelta
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams
open Internal.Utilities

module ILWriter = FSharp.Compiler.AbstractIL.ILBinaryWriter

/// Represents the emitted artifacts for a hot reload delta.
type IlxDelta =
    {
        Metadata: byte[]
        IL: byte[]
        Pdb: byte[] option
        EncLog: (TableIndex * int * EditAndContinueOperation) array
        EncMap: (TableIndex * int) array
        UpdatedTypeTokens: int list
        UpdatedMethodTokens: int list
        MethodBodies: MethodBodyUpdate list
        StandaloneSignatures: StandaloneSignatureUpdate list
    }

/// Request payload used when producing a delta. This will accumulate more fields as the emitter is implemented.
type IlxDeltaRequest =
    {
        Baseline: FSharpEmitBaseline
        UpdatedTypes: string list
        UpdatedMethods: MethodDefinitionKey list
        Module: ILModuleDef
        SymbolChanges: FSharpSymbolChanges option
    }

/// Helper that produces an empty delta payload.
let private emptyDelta: IlxDelta =
    {
        Metadata = Array.empty
        IL = Array.empty
        Pdb = None
        EncLog = Array.empty
        EncMap = Array.empty
        UpdatedTypeTokens = []
        UpdatedMethodTokens = []
        MethodBodies = []
        StandaloneSignatures = []
    }

let private defaultWriterOptions (ilg: ILGlobals) (checksumAlgorithm: HashAlgorithm) : ILWriter.options =
    {
        ilg = ilg
        outfile = Path.Combine(Path.GetTempPath(), "fsharp-hotreload-delta.dll")
        pdbfile = None
        portablePDB = true
        embeddedPDB = false
        embedAllSource = false
        embedSourceList = []
        allGivenSources = []
        sourceLink = ""
        checksumAlgorithm = checksumAlgorithm
        signer = None
        emitTailcalls = false
        deterministic = true
        dumpDebugInfo = false
        referenceAssemblyOnly = false
        referenceAssemblyAttribOpt = None
        referenceAssemblySignatureHash = None
        pathMap = PathMap.empty
    }

/// Emits the delta artifacts for a request. The current implementation populates token projections
/// while leaving the raw metadata/IL/PDB payload empty; future work will replace the placeholders
/// with fully emitted heaps.
let emitDelta (request: IlxDeltaRequest) : IlxDelta =
    let typeIndex =
        let comparer = StringComparer.Ordinal
        let dict = Dictionary<string, struct (ILTypeDef list * ILTypeDef)>(comparer)

        let rec walk (enclosing: ILTypeDef list) (tdef: ILTypeDef) =
            let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, tdef)
            dict[typeRef.FullName] <- struct (enclosing, tdef)
            for nested in tdef.NestedTypes.AsList() do
                walk (enclosing @ [ tdef ]) nested

        request.Module.TypeDefs.AsList() |> List.iter (walk [])
        dict

    let tryResolveMethod (typeDef: ILTypeDef) (key: MethodDefinitionKey) =
        typeDef.Methods.AsList()
        |> List.tryFind (fun mdef ->
            mdef.Name = key.Name
            && mdef.GenericParams.Length = key.GenericArity
            && mdef.ParameterTypes = key.ParameterTypes
            && mdef.Return.Type = key.ReturnType)

    let resolvedMethods =
        request.UpdatedMethods
        |> List.choose (fun key ->
            match typeIndex.TryGetValue key.DeclaringType with
            | true, struct (enclosing, typeDef) ->
                match tryResolveMethod typeDef key with
                | Some methodDef -> Some(enclosing, typeDef, methodDef, key)
                | None -> None
            | _ -> None)

    let symbolChangeTypeNames =
        request.SymbolChanges
        |> Option.map FSharpSymbolChanges.entitySymbolsWithChanges
        |> Option.defaultValue []
        |> List.map (fun symbol -> symbol.QualifiedName)

    let builder = IlDeltaStreamBuilder()

    let primaryScopeRef =
        match request.Module.Manifest with
        | Some manifest ->
            let publicKey =
                manifest.PublicKey |> Option.map (fun key -> PublicKey.KeyAsToken key)
            let asmRef =
                ILAssemblyRef.Create(
                    manifest.Name,
                    None,
                    publicKey,
                    manifest.Retargetable,
                    manifest.Version,
                    manifest.Locale
                )

            ILScopeRef.Assembly asmRef
        | None -> ILScopeRef.PrimaryAssembly

    let fsharpCoreScopeRef =
        ILScopeRef.Assembly (ILAssemblyRef.Create("FSharp.Core", None, None, false, None, None))

    let ilg = mkILGlobals (primaryScopeRef, [], fsharpCoreScopeRef)

    let writerOptions =
        defaultWriterOptions ilg HashAlgorithm.Sha256
    let assemblyBytes, _, _, _ = ILWriter.WriteILBinaryInMemoryWithArtifacts(writerOptions, request.Module, id)

    use peStream = new MemoryStream(assemblyBytes, writable = false)
    use peReader = new PEReader(peStream)
    let metadataReader = peReader.GetMetadataReader()

    let moduleDef = metadataReader.GetModuleDefinition()
    let moduleName = metadataReader.GetString(moduleDef.Name)
    let moduleMvid =
        if moduleDef.Mvid.IsNil then Guid.NewGuid()
        else metadataReader.GetGuid(moduleDef.Mvid)
    let encBaseId = moduleMvid
    let encId = Guid.NewGuid()

    let getMethodToken key = request.Baseline.MethodTokens |> Map.tryFind key

    resolvedMethods
    |> List.iter (fun (_, _, _, key) ->
        match getMethodToken key with
        | None -> ()
        | Some methodToken ->
            let methodHandle = MetadataTokens.MethodDefinitionHandle methodToken
            if not methodHandle.IsNil then
                let methodDef = metadataReader.GetMethodDefinition methodHandle
                let body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress)
                let ilBytes = body.GetILBytes() |> Seq.toArray
                let localSigToken =
                    if body.LocalSignature.IsNil then
                        0
                    else
                        let standaloneSignature = metadataReader.GetStandaloneSignature(body.LocalSignature)
                        let sigBytes = metadataReader.GetBlobBytes(standaloneSignature.Signature)
                        builder.AddStandaloneSignature(sigBytes)

                builder.AddMethodBody(methodToken, localSigToken, ilBytes))

    let updatedTypeTokens =
        let methodTypeNames =
            resolvedMethods
            |> List.map (fun (enclosing, typeDef, _, _) ->
                let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
                typeRef.FullName)

        (request.UpdatedTypes @ symbolChangeTypeNames @ methodTypeNames)
        |> List.distinct
        |> List.choose (fun typeName -> request.Baseline.TypeTokens |> Map.tryFind typeName)

    let updatedMethodTokens =
        resolvedMethods
        |> List.choose (fun (_, _, _, key) -> request.Baseline.MethodTokens |> Map.tryFind key)

    updatedTypeTokens
    |> List.iter (fun token ->
        let row = token &&& 0x00FFFFFF
        builder.AddEncLogEntry(TableIndex.TypeDef, row, EditAndContinueOperation.Default)
        builder.AddEncMapEntry(TableIndex.TypeDef, row))

    updatedMethodTokens
    |> List.iter (fun token ->
        let row = token &&& 0x00FFFFFF
        builder.AddEncLogEntry(TableIndex.MethodDef, row, EditAndContinueOperation.Default)
        builder.AddEncMapEntry(TableIndex.MethodDef, row))

    let streams = builder.Build(moduleName, moduleMvid, encId, Some encBaseId)

    { emptyDelta with
        Metadata = streams.Metadata
        IL = streams.IL
        UpdatedTypeTokens = updatedTypeTokens
        UpdatedMethodTokens = updatedMethodTokens
        EncLog = streams.EncLogEntries |> List.toArray
        EncMap = streams.EncMapEntries |> List.toArray
        MethodBodies = streams.MethodBodies
        StandaloneSignatures = streams.StandaloneSignatures
    }
