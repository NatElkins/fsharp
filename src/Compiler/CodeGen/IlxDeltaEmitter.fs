module internal FSharp.Compiler.IlxDeltaEmitter

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Linq
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection
open System.Reflection.Emit
open System.Reflection.PortableExecutable
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILDelta
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReload.SymbolMatcher
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.HotReloadPdb
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.SynthesizedTypeMaps
open FSharp.Compiler.Syntax.PrettyNaming
open Internal.Utilities

exception HotReloadUnsupportedEditException of string

module ILWriter = FSharp.Compiler.AbstractIL.ILBinaryWriter
let private normalizeGeneratedFieldName (name: string) =
    match name.IndexOf('@') with
    | -1 -> name
    | idx when idx > 0 -> name.Substring(0, idx)
    | _ -> name

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
        GenerationId: Guid
        BaseGenerationId: Guid
        UserStringUpdates: (int * int * string) list
    }

type private MethodMetadataUpdate =
    {
        MethodToken: int
        MethodHandle: MethodDefinitionHandle
        Body: MethodBodyUpdate
    }

/// Request payload used when producing a delta. This will accumulate more fields as the emitter is implemented.
type IlxDeltaRequest =
    {
        Baseline: FSharpEmitBaseline
        UpdatedTypes: string list
        UpdatedMethods: MethodDefinitionKey list
        Module: ILModuleDef
        SymbolChanges: FSharpSymbolChanges option
        CurrentGeneration: int
        PreviousGenerationId: Guid option
        SynthesizedNames: FSharpSynthesizedTypeMaps option
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
        GenerationId = Guid.Empty
        BaseGenerationId = Guid.Empty
        UserStringUpdates = []
    }

let private defaultWriterOptions (ilg: ILGlobals) (checksumAlgorithm: HashAlgorithm) : ILWriter.options =
    // ILBinaryWriter insists on having an output path even when we emit to memory. Generate a
    // unique, throwaway file name per invocation so parallel sessions never collide, and so we
    // leave a breadcrumb for debugging when traces mention the synthetic assembly.
    let scratchDll =
        let fileName = sprintf "fsharp-hotreload-%s.dll" (Guid.NewGuid().ToString("N"))
        Path.Combine(Path.GetTempPath(), fileName)

    let scratchPdb =
        match Path.ChangeExtension(scratchDll, ".pdb") with
        | null -> scratchDll + ".pdb"
        | path -> path

    {
        ilg = ilg
        outfile = scratchDll
        pdbfile = Some scratchPdb
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

let private opCodeLookup : Lazy<Dictionary<int, OpCode>> =
    lazy
        (let dict = Dictionary<int, OpCode>()
         for field in typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
             let op = field.GetValue(null) :?> OpCode
             let value = int (uint16 op.Value)
             if not (dict.ContainsKey(value)) then
                 dict[value] <- op
         dict)

let private rewriteMethodBody (remapUserString: int -> int) (remapEntityToken: int -> int) (body: MethodBodyBlock) =
    let ilBytes = body.GetILBytes().ToArray()
    let rewritten = Array.copy ilBytes
    let mutable offset = 0
    let length = ilBytes.Length

    let advance count = offset <- offset + count

    while offset < length do
        let opcodeValue, size =
            let first = int ilBytes.[offset]
            if first = 0xFE then
                let second = int ilBytes.[offset + 1]
                ((0xFE00 ||| second), 2)
            else
                (first, 1)
        advance size

        let operandType =
            match opCodeLookup.Value.TryGetValue opcodeValue with
            | true, op -> op.OperandType
            | _ -> OperandType.InlineNone

        let operandStart = offset

        let inline readInt32 () =
            let value = BitConverter.ToInt32(ilBytes, operandStart)
            advance 4
            value

        let inline readInt16 () =
            let value = BitConverter.ToInt16(ilBytes, operandStart)
            advance 2
            value

        let inline readSByte () =
            let value = sbyte ilBytes.[operandStart]
            advance 1
            value

        let inline readByte () =
            let value = ilBytes.[operandStart]
            advance 1
            value

        match operandType with
        | OperandType.InlineNone -> ()
        | OperandType.ShortInlineI -> readSByte () |> ignore
        | OperandType.InlineI -> readInt32 () |> ignore
        | OperandType.InlineI8 -> advance 8
        | OperandType.ShortInlineR -> advance 4
        | OperandType.InlineR -> advance 8
        | OperandType.InlineBrTarget -> readInt32 () |> ignore
        | OperandType.ShortInlineBrTarget -> readSByte () |> ignore
        | OperandType.ShortInlineVar -> readByte () |> ignore
        | OperandType.InlineVar -> readInt16 () |> ignore
        | OperandType.InlineString ->
            let original = readInt32 ()
            let updated = remapUserString original
            let tokenBytes = BitConverter.GetBytes(updated : int)
            Buffer.BlockCopy(tokenBytes, 0, rewritten, operandStart, 4)
        | OperandType.InlineField
        | OperandType.InlineMethod
        | OperandType.InlineSig
        | OperandType.InlineTok
        | OperandType.InlineType ->
            let original = readInt32 ()
            let updated = remapEntityToken original
            if original <> updated then
                let tokenBytes = BitConverter.GetBytes(updated : int)
                Buffer.BlockCopy(tokenBytes, 0, rewritten, operandStart, 4)
        | OperandType.InlineSwitch ->
            let count = readInt32 ()
            advance (count * 4)
        | OperandType.InlinePhi ->
            let count = int (readByte ())
            advance (count * 2)
        | _ -> ()

    rewritten

/// Emits the delta artifacts for a request. The current implementation populates token projections
/// while leaving the raw metadata/IL/PDB payload empty; future work will replace the placeholders
/// with fully emitted heaps.
let emitDelta (request: IlxDeltaRequest) : IlxDelta =
    let synthesizedBuckets =
        request.SynthesizedNames
        |> Option.map (fun map ->
            map.Snapshot
            |> Seq.map (fun (basic, names) -> basic, names)
            |> dict)

    let symbolMatcher =
        match request.SynthesizedNames with
        | Some map -> FSharpSymbolMatcher.createWithSynthesizedNames request.Module map
        | None -> FSharpSymbolMatcher.create request.Module

    let resolvedMethods =
        request.UpdatedMethods
        |> List.choose (fun key ->
            match FSharpSymbolMatcher.tryGetMethodDef symbolMatcher key with
            | Some(enclosing, typeDef, methodDef) -> Some(enclosing, typeDef, methodDef, key)
            | None -> None)

    let symbolChangeTypeNames =
        request.SymbolChanges
        |> Option.map FSharpSymbolChanges.entitySymbolsWithChanges
        |> Option.defaultValue []
        |> List.map (fun symbol -> symbol.QualifiedName)

    let builder = IlDeltaStreamBuilder(Some request.Baseline.Metadata)

    let baselineTypeTokens = request.Baseline.TypeTokens

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

    let traceUserStringUpdates =
        lazy (
            match System.Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_STRINGS") with
            | null -> false
            | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
            | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
            | _ -> false
        )
    let traceSynthesizedMappings =
        lazy (
            match System.Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_SYNTHESIZED") with
            | null -> false
            | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
            | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
            | _ -> false
        )
    let traceMethodUpdates =
        lazy (
            match System.Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METHODS") with
            | null -> false
            | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
            | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
            | _ -> false
        )

    let writerOptions = defaultWriterOptions ilg HashAlgorithm.Sha256
    let assemblyBytes, pdbBytesOpt, emittedTokenMappings, _ =
        ILWriter.WriteILBinaryInMemoryWithArtifacts(writerOptions, request.Module, id)
    if traceUserStringUpdates.Value then
        try
            let tempDll =
                Path.Combine(Path.GetTempPath(), $"fsharp-hotreload-ilmodule-{Guid.NewGuid():N}.dll")
            File.WriteAllBytes(tempDll, assemblyBytes)
            printfn "[fsharp-hotreload][trace] wrote IL module snapshot to %s" tempDll
        with ex ->
            printfn "[fsharp-hotreload][trace] failed to write IL module snapshot: %s" ex.Message

    use peStream = new MemoryStream(assemblyBytes, writable = false)
    use peReader = new PEReader(peStream)
    let metadataReader = peReader.GetMetadataReader()
    let metadataBuilder = builder.MetadataBuilder
    let stringTokenCache = Dictionary<int, int>()
    let userStringUpdates = ResizeArray<int * int * string>()

    let logUserString originalToken newToken text =
        if traceUserStringUpdates.Value then
            printfn "[fsharp-hotreload][userstring] original=0x%08X new=0x%08X text=%s" originalToken newToken text
    if traceUserStringUpdates.Value then
        for (_, _, methodDef, _) in resolvedMethods do
            match methodDef.Code with
            | None -> ()
            | Some code ->
                for instr in code.Instrs do
                    match instr with
                    | I_ldstr literal ->
                        printfn "[fsharp-hotreload][method] %s ldstr literal=%s" methodDef.Name literal
                    | _ -> ()

    let remapUserString token =
        match stringTokenCache.TryGetValue token with
        | true, mapped -> mapped
        | _ ->
            let handle = MetadataTokens.UserStringHandle token
            let value = metadataReader.GetUserString handle
            let newHandle = metadataBuilder.GetOrAddUserString value
            let newToken = MetadataTokens.GetToken newHandle
            stringTokenCache[token] <- newToken
            userStringUpdates.Add((token, newToken, value))
            logUserString token newToken value
            newToken

    let typeTokenMap = Dictionary<int, int>()
    let fieldTokenMap = Dictionary<int, int>()
    let methodTokenMap = Dictionary<int, int>()
    let propertyTokenMap = Dictionary<int, int>()
    let eventTokenMap = Dictionary<int, int>()
    let baselineTypeNameByNew = Dictionary<string, string>(StringComparer.Ordinal)

    let getAliasCandidates (typeName: string) =
        match synthesizedBuckets with
        | Some buckets when IsCompilerGeneratedName typeName ->
            let basicName = GetBasicNameOfPossibleCompilerGeneratedName typeName
            match buckets.TryGetValue basicName with
            | true, aliases when aliases.Length > 0 ->
                if aliases |> Array.exists (fun alias -> alias = typeName) then
                    aliases
                else
                    Array.append [| typeName |] aliases
            | _ -> [| typeName |]
        | _ -> [| typeName |]

    let resolveBaselineTypeFullName (enclosing: ILTypeDef list) (typeDef: ILTypeDef) =
        let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
        let newFullName = typeRef.FullName

        let parentBaselinePrefixOpt =
            match enclosing with
            | [] -> None
            | _ ->
                let parentType = List.last enclosing
                let parentEnclosing = enclosing |> List.take (List.length enclosing - 1)
                let parentRef = mkRefForNestedILTypeDef ILScopeRef.Local (parentEnclosing, parentType)
                let parentBaseline =
                    match baselineTypeNameByNew.TryGetValue parentRef.FullName with
                    | true, baselineParent -> baselineParent
                    | _ -> parentRef.FullName
                Some(parentBaseline + "+")

        let basePrefix =
            match parentBaselinePrefixOpt with
            | Some prefix -> prefix
            | None ->
                let lastDot = newFullName.LastIndexOf('.')
                if lastDot >= 0 then
                    newFullName.Substring(0, lastDot + 1)
                else
                    ""

        let candidateNames =
            let aliases = getAliasCandidates typeDef.Name
            let prefixes =
                if basePrefix.EndsWith("+", StringComparison.Ordinal) then
                    let withoutPlus = basePrefix.Substring(0, basePrefix.Length - 1)
                    let dotPrefix = if String.IsNullOrEmpty withoutPlus then "" else withoutPlus + "."
                    [| basePrefix; dotPrefix |]
                else
                    [| basePrefix |]

            let projected =
                prefixes
                |> Array.collect (fun prefix ->
                    aliases
                    |> Array.map (fun alias ->
                        if prefix.EndsWith("+", StringComparison.Ordinal) || prefix.EndsWith(".", StringComparison.Ordinal) then
                            prefix + alias
                        elif prefix = "" then
                            alias
                        else
                            prefix + alias))

            Array.concat
                [| projected
                   prefixes
                   |> Array.collect (fun prefix ->
                       [|
                           if prefix.EndsWith("+", StringComparison.Ordinal) || prefix.EndsWith(".", StringComparison.Ordinal) then
                               yield prefix + typeDef.Name
                           elif prefix = "" then
                               yield typeDef.Name
                           else
                               yield prefix + typeDef.Name
                       |])
                   [| newFullName |] |]
            |> Array.filter (fun name -> not (String.IsNullOrWhiteSpace name))
            |> Array.distinct

        let baselineNameOpt =
            candidateNames
            |> Array.tryPick (fun candidate ->
                match request.Baseline.TypeTokens |> Map.tryFind candidate with
                | Some token -> Some(candidate, token)
                | None -> None)

        if traceSynthesizedMappings.Value then
            match baselineNameOpt with
            | Some (baselineName, _) when not (String.Equals(newFullName, baselineName, StringComparison.Ordinal)) ->
                printfn "[fsharp-hotreload][synthesized-map] %s -> %s" newFullName baselineName
            | None ->
                printfn "[fsharp-hotreload][synthesized-map] no baseline match for %s candidates=%A" newFullName candidateNames
            | _ -> ()

        let baselineName, baselineTokenOpt =
            match baselineNameOpt with
            | Some (baseline, token) -> baseline, Some token
            | None -> newFullName, None

        baselineTypeNameByNew[newFullName] <- baselineName
        if traceSynthesizedMappings.Value then
            printfn "[fsharp-hotreload][synthesized-map] stored %s -> %s" newFullName baselineName
        baselineName, baselineTokenOpt

    let tryGetBaselineTypeName fullName =
        match baselineTypeNameByNew.TryGetValue fullName with
        | true, baseline -> baseline
        | _ -> fullName

    let tryGetBaselineTypeToken (enclosing: ILTypeDef list) (typeDef: ILTypeDef) =
        let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
        let baselineName = tryGetBaselineTypeName typeRef.FullName
        baselineTypeTokens |> Map.tryFind baselineName

    let addMapping (dict: Dictionary<int, int>) newToken baselineToken =
        if newToken <> 0 && baselineToken <> 0 && newToken <> baselineToken then
            dict[newToken] <- baselineToken

    let rec collectTypeMappings (enclosing: ILTypeDef list) (typeDef: ILTypeDef) =
        let newTypeToken = emittedTokenMappings.TypeDefTokenMap(enclosing, typeDef)
        if traceSynthesizedMappings.Value then
            let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
            printfn "[fsharp-hotreload][synthesized-map] visiting %s" typeRef.FullName
        let _, baselineTokenOpt = resolveBaselineTypeFullName enclosing typeDef
        let baselineTypeToken =
            match baselineTokenOpt with
            | Some token -> token
            | None ->
                match tryGetBaselineTypeToken enclosing typeDef with
                | Some token -> token
                | None -> request.Baseline.TokenMappings.TypeDefTokenMap(enclosing, typeDef)
        addMapping typeTokenMap newTypeToken baselineTypeToken

        typeDef.Fields.AsList()
        |> List.iter (fun fieldDef ->
            let declaringTypeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
            let baselineDeclaringType = tryGetBaselineTypeName declaringTypeRef.FullName
            let fieldKey: FieldDefinitionKey =
                { DeclaringType = baselineDeclaringType
                  Name = fieldDef.Name
                  FieldType = fieldDef.FieldType }

            let baselineFieldTokenOpt =
                match request.Baseline.FieldTokens |> Map.tryFind fieldKey with
                | Some token -> Some token
                | None ->
                    let sanitizedTarget = normalizeGeneratedFieldName fieldDef.Name
                    request.Baseline.FieldTokens
                    |> Map.tryPick (fun key token ->
                        if key.DeclaringType = baselineDeclaringType && key.FieldType = fieldDef.FieldType then
                            if normalizeGeneratedFieldName key.Name = sanitizedTarget then
                                Some token
                            else
                                None
                        else
                            None)

            match baselineFieldTokenOpt with
            | Some baselineFieldToken ->
                let newFieldToken = emittedTokenMappings.FieldDefTokenMap(enclosing, typeDef) fieldDef
                addMapping fieldTokenMap newFieldToken baselineFieldToken
            | None when synthesizedBuckets.IsSome && IsCompilerGeneratedName typeDef.Name -> ()
            | None ->
                let fieldDisplay = $"{declaringTypeRef.FullName}::{fieldDef.Name}"
                let message =
                    $"Edit adds field '{fieldDisplay}'. Hot reload currently supports method-body changes only; please rebuild."
                raise (HotReloadUnsupportedEditException message))

        typeDef.Methods.AsList()
        |> List.iter (fun methodDef ->
            let newMethodToken = emittedTokenMappings.MethodDefTokenMap(enclosing, typeDef) methodDef
            let declaringTypeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
            let baselineDeclaringType = tryGetBaselineTypeName declaringTypeRef.FullName
            let methodKey: MethodDefinitionKey =
                { DeclaringType = baselineDeclaringType
                  Name = methodDef.Name
                  GenericArity = methodDef.GenericParams.Length
                  ParameterTypes = methodDef.ParameterTypes
                  ReturnType = methodDef.Return.Type }

            match request.Baseline.MethodTokens |> Map.tryFind methodKey with
            | Some baselineMethodToken ->
                addMapping methodTokenMap newMethodToken baselineMethodToken
            | None when synthesizedBuckets.IsSome && IsCompilerGeneratedName typeDef.Name -> ()
            | None ->
                let methodDisplay = $"{baselineDeclaringType}::{methodDef.Name}"
                let message =
                    $"Edit adds method '{methodDisplay}'. Hot reload currently supports method-body changes only; please rebuild."
                raise (HotReloadUnsupportedEditException message))

        typeDef.Properties.AsList()
        |> List.iter (fun propertyDef ->
            let newPropertyToken = emittedTokenMappings.PropertyTokenMap(enclosing, typeDef) propertyDef
            let declaringTypeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
            let baselineDeclaringType = tryGetBaselineTypeName declaringTypeRef.FullName
            let propertyKey: PropertyDefinitionKey =
                { DeclaringType = baselineDeclaringType
                  Name = propertyDef.Name
                  PropertyType = propertyDef.PropertyType
                  IndexParameterTypes = List.ofSeq propertyDef.Args }

            match request.Baseline.PropertyTokens |> Map.tryFind propertyKey with
            | Some baselinePropertyToken ->
                addMapping propertyTokenMap newPropertyToken baselinePropertyToken
            | None when synthesizedBuckets.IsSome && IsCompilerGeneratedName typeDef.Name -> ()
            | None ->
                let propertyDisplay = $"{baselineDeclaringType}::{propertyDef.Name}"
                let message =
                    $"Edit adds property '{propertyDisplay}'. Hot reload currently supports method-body changes only; please rebuild."
                raise (HotReloadUnsupportedEditException message))

        typeDef.Events.AsList()
        |> List.iter (fun eventDef ->
            let newEventToken = emittedTokenMappings.EventTokenMap(enclosing, typeDef) eventDef
            let declaringTypeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
            let baselineDeclaringType = tryGetBaselineTypeName declaringTypeRef.FullName
            let eventKey: EventDefinitionKey =
                { DeclaringType = baselineDeclaringType
                  Name = eventDef.Name
                  EventType = eventDef.EventType }

            match request.Baseline.EventTokens |> Map.tryFind eventKey with
            | Some baselineEventToken ->
                addMapping eventTokenMap newEventToken baselineEventToken
            | None when synthesizedBuckets.IsSome && IsCompilerGeneratedName typeDef.Name -> ()
            | None ->
                let eventDisplay = $"{baselineDeclaringType}::{eventDef.Name}"
                let message =
                    $"Edit adds event '{eventDisplay}'. Hot reload currently supports method-body changes only; please rebuild."
                raise (HotReloadUnsupportedEditException message))

        typeDef.NestedTypes.AsList()
        |> List.iter (fun nested -> collectTypeMappings (enclosing @ [ typeDef ]) nested)

    request.Module.TypeDefs.AsList()
    |> List.iter (collectTypeMappings [])

    let inline remapWith (dict: Dictionary<int, int>) token =
        match dict.TryGetValue token with
        | true, mapped -> mapped
        | _ -> token

    let remapEntityToken token =
        match token &&& 0xFF000000 with
        | 0x02000000 -> remapWith typeTokenMap token
        | 0x04000000 -> remapWith fieldTokenMap token
        | 0x06000000 -> remapWith methodTokenMap token
        | 0x14000000 -> remapWith eventTokenMap token
        | 0x17000000 -> remapWith propertyTokenMap token
        | _ -> token

    let moduleDef = metadataReader.GetModuleDefinition()
    let moduleName = metadataReader.GetString(moduleDef.Name)
    let moduleMvid = request.Baseline.ModuleId

    let baseGenerationId =
        match request.CurrentGeneration, request.PreviousGenerationId with
        | 1, _ -> request.Baseline.ModuleId
        | _, Some prev -> prev
        | _, None -> request.Baseline.ModuleId

    let encBaseId = baseGenerationId
    let encId = Guid.NewGuid()

    let methodUpdateInputs =
        resolvedMethods
        |> List.choose (fun (_, _, _, key) ->
            match request.Baseline.MethodTokens |> Map.tryFind key with
            | None -> None
            | Some methodToken ->
                let methodHandle = MetadataTokens.MethodDefinitionHandle methodToken
                if methodHandle.IsNil then
                    None
                else
                    let methodDef = metadataReader.GetMethodDefinition methodHandle
                    let body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress)
                    if traceMethodUpdates.Value then
                        printfn
                            "[fsharp-hotreload][method-update] %s::%s token=0x%08X"
                            key.DeclaringType
                            key.Name
                            methodToken
                    Some(struct (key, methodToken, methodHandle, methodDef, body)))

    let updatedTypeTokens =
        let methodTypeNames =
            resolvedMethods
            |> List.map (fun (enclosing, typeDef, _, _) ->
                let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
                tryGetBaselineTypeName typeRef.FullName)

        (request.UpdatedTypes @ symbolChangeTypeNames @ methodTypeNames)
        |> List.map tryGetBaselineTypeName
        |> List.distinct
        |> List.choose (fun typeName -> request.Baseline.TypeTokens |> Map.tryFind typeName)

    let updatedMethodTokens =
        methodUpdateInputs
        |> List.map (fun struct (_, methodToken, _, _, _) -> methodToken)

    if List.isEmpty methodUpdateInputs && List.isEmpty updatedTypeTokens then
        emptyDelta
    else
        let metadataBuilder = builder.MetadataBuilder

        builder.AddEncLogEntry(TableIndex.Module, 1, EditAndContinueOperation.Default)
        builder.AddEncMapEntry(TableIndex.Module, 1)

        let methodUpdatesWithDefs =
            methodUpdateInputs
            |> List.map (fun struct (_, methodToken, methodHandle, methodDef, body) ->
                let ilBytes = rewriteMethodBody remapUserString remapEntityToken body
                let localSigToken =
                    if body.LocalSignature.IsNil then
                        0
                    else
                        let handle = EntityHandle.op_Implicit body.LocalSignature
                        MetadataTokens.GetToken(handle)

                let bodyUpdate =
                    builder.AddMethodBody(
                        methodToken,
                        localSigToken,
                        ilBytes,
                        body.MaxStack,
                        body.LocalVariablesInitialized,
                        body.ExceptionRegions,
                        remapEntityToken
                    )

                let rowId = methodToken &&& 0x00FFFFFF
                builder.AddEncLogEntry(TableIndex.MethodDef, rowId, EditAndContinueOperation.Default)
                builder.AddEncMapEntry(TableIndex.MethodDef, rowId)

                ({ MethodToken = methodToken
                   MethodHandle = methodHandle
                   Body = bodyUpdate }, methodDef))

        let methodUpdates = methodUpdatesWithDefs |> List.map fst

        metadataBuilder.SetCapacity(TableIndex.MethodDef, methodUpdates.Length)

        methodUpdatesWithDefs
        |> List.iter (fun (update, methodDef) ->
            let methodName = metadataReader.GetString methodDef.Name
            let nameHandle = metadataBuilder.GetOrAddString methodName
            let signatureBytes = metadataReader.GetBlobBytes methodDef.Signature
            let signatureHandle = metadataBuilder.GetOrAddBlob signatureBytes

            metadataBuilder.AddMethodDefinition(
                methodDef.Attributes,
                methodDef.ImplAttributes,
                nameHandle,
                signatureHandle,
                update.Body.CodeOffset,
                ParameterHandle()
            )
            |> ignore)

        let streams = builder.Build(moduleName, moduleMvid, encId, Some encBaseId)

        let pdbDelta =
            match pdbBytesOpt with
            | None -> None
            | Some pdbBytes -> HotReloadPdb.emitDelta request.Baseline pdbBytes updatedMethodTokens

        { emptyDelta with
            Metadata = streams.Metadata
            IL = streams.IL
            UpdatedTypeTokens = updatedTypeTokens
            UpdatedMethodTokens = updatedMethodTokens
            EncLog = streams.EncLogEntries |> List.toArray
            EncMap = streams.EncMapEntries |> List.toArray
            MethodBodies = streams.MethodBodies
            StandaloneSignatures = streams.StandaloneSignatures
            Pdb = pdbDelta
            GenerationId = encId
            BaseGenerationId = encBaseId
            UserStringUpdates = userStringUpdates |> Seq.toList
        }
        |> fun delta ->
            if traceUserStringUpdates.Value then
                for (original, updated, text) in delta.UserStringUpdates do
                    printfn "[fsharp-hotreload][userstring-summary] original=0x%08X new=0x%08X text=%s" original updated text
            delta
