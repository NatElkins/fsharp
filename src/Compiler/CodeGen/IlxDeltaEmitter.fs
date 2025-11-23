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
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReload.SymbolMatcher
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.HotReloadPdb
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.CodeGen.FSharpDefinitionIndex
open FSharp.Compiler.SynthesizedTypeMaps
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.TypedTreeDiff
open Internal.Utilities

module MetadataWriter = FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter
open MetadataWriter
open FSharp.Compiler.CodeGen.DeltaMetadataTables
open FSharp.Compiler.CodeGen.DeltaMetadataTypes

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
        AddedOrChangedMethods: HotReloadBaseline.AddedOrChangedMethodInfo list
        MethodBodies: MethodBodyUpdate list
        StandaloneSignatures: StandaloneSignatureUpdate list
        GenerationId: Guid
        BaseGenerationId: Guid
        UserStringUpdates: (int * int * string) list
        MethodDefinitionRows: MethodDefinitionRowInfo list
        UpdatedBaseline: FSharpEmitBaseline option
    }

/// Request payload used when producing a delta. This will accumulate more fields as the emitter is implemented.
type IlxDeltaRequest =
    {
        Baseline: FSharpEmitBaseline
        UpdatedTypes: string list
        UpdatedMethods: MethodDefinitionKey list
        UpdatedAccessors: AccessorUpdate list
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
        AddedOrChangedMethods = []
        MethodBodies = []
        StandaloneSignatures = []
        GenerationId = Guid.Empty
        BaseGenerationId = Guid.Empty
        UserStringUpdates = []
        MethodDefinitionRows = []
        UpdatedBaseline = None
    }

let private defaultWriterOptions (ilg: ILGlobals) (checksumAlgorithm: HashAlgorithm) : ILWriter.options =
    // ILBinaryWriter insists on having an output path even when we emit to memory. Generate a
    // unique, throwaway file name per invocation so parallel sessions never collide, and so we
    // leave a breadcrumb for debugging when traces mention the synthetic assembly.
    let scratchDll =
        let fileName = sprintf "fsharp-hotreload-%s.dll" (System.Guid.NewGuid().ToString("N"))
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
    let traceMetadata =
        lazy (
            match System.Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METADATA") with
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
                Path.Combine(Path.GetTempPath(), $"fsharp-hotreload-ilmodule-{System.Guid.NewGuid():N}.dll")
            File.WriteAllBytes(tempDll, assemblyBytes)
            printfn "[fsharp-hotreload][trace] wrote IL module snapshot to %s" tempDll
        with ex ->
            printfn "[fsharp-hotreload][trace] failed to write IL module snapshot: %s" ex.Message

    use peStream = new MemoryStream(assemblyBytes, writable = false)
    use peReader = new PEReader(peStream)
    let metadataReader = peReader.GetMetadataReader()
    let moduleDef = metadataReader.GetModuleDefinition()
    let moduleName = metadataReader.GetString moduleDef.Name
    let baselineModuleNameHandle = request.Baseline.ModuleNameHandle
    let metadataBuilder = builder.MetadataBuilder
    let stringTokenCache = Dictionary<int, int>()
    let userStringUpdates = ResizeArray<int * int * string>()

    let logUserString originalToken newToken text =
        if traceUserStringUpdates.Value then
            printfn "[fsharp-hotreload][userstring] original=0x%08X new=0x%08X text=%s" originalToken newToken text
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
    let addedMethodTokens = Dictionary<MethodDefinitionKey, int>(HashIdentity.Structural)
    let addedPropertyTokens = Dictionary<PropertyDefinitionKey, int>(HashIdentity.Structural)
    let addedEventTokens = Dictionary<EventDefinitionKey, int>(HashIdentity.Structural)
    let addedPropertyTokenLookup = Dictionary<int, PropertyDefinitionKey>()
    let addedEventTokenLookup = Dictionary<int, EventDefinitionKey>()
    let propertyHandleLookup = Dictionary<PropertyDefinitionKey, PropertyDefinitionHandle>()
    let eventHandleLookup = Dictionary<EventDefinitionKey, EventDefinitionHandle>()
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
                if not (addedMethodTokens.ContainsKey methodKey) then
                    addedMethodTokens[methodKey] <- newMethodToken)

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
                if not (addedPropertyTokens.ContainsKey propertyKey) then
                    addedPropertyTokens[propertyKey] <- newPropertyToken
                    addedPropertyTokenLookup[newPropertyToken] <- propertyKey
                    let handleRow = newPropertyToken &&& 0x00FFFFFF
                    propertyHandleLookup[propertyKey] <- MetadataTokens.PropertyDefinitionHandle handleRow)

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
                if not (addedEventTokens.ContainsKey eventKey) then
                    addedEventTokens[eventKey] <- newEventToken
                    addedEventTokenLookup[newEventToken] <- eventKey
                    let handleRow = newEventToken &&& 0x00FFFFFF
                    eventHandleLookup[eventKey] <- MetadataTokens.EventDefinitionHandle handleRow)

        typeDef.NestedTypes.AsList()
        |> List.iter (fun nested -> collectTypeMappings (enclosing @ [ typeDef ]) nested)

    request.Module.TypeDefs.AsList()
    |> List.iter (collectTypeMappings [])

    let addedMethodKeys =
        addedMethodTokens
        |> Seq.map (fun kvp -> kvp.Key)
        |> Seq.toList

    let dedupeMethodKeys (keys: MethodDefinitionKey list) =
        let seen = HashSet<MethodDefinitionKey>(HashIdentity.Structural)
        keys
        |> List.fold (fun acc key -> if seen.Add key then key :: acc else acc) []
        |> List.rev

    let allUpdatedMethods =
        (request.UpdatedMethods @ addedMethodKeys)
        |> dedupeMethodKeys

    let resolvedMethods =
        allUpdatedMethods
        |> List.choose (fun key ->
            match FSharpSymbolMatcher.tryGetMethodDef symbolMatcher key with
            | Some(enclosing, typeDef, methodDef) -> Some(enclosing, typeDef, methodDef, key)
            | None -> None)

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
    let moduleMvid = request.Baseline.ModuleId

    let encBaseId =
        match request.PreviousGenerationId with
        | Some prev when prev <> Guid.Empty -> prev
        | _ ->
            let baselineEncId = request.Baseline.EncId
            if baselineEncId <> Guid.Empty then baselineEncId else Guid.Empty

    if traceMetadata.Value then
        printfn
            "[fsharp-hotreload][metadata] generation=%d prevGeneration=%A baselineEncId=%A resolvedBase=%A"
            request.CurrentGeneration
            request.PreviousGenerationId
            request.Baseline.EncId
            encBaseId
    let encId = System.Guid.NewGuid()

    let methodRowLookup =
        let baselineTokens = request.Baseline.MethodTokens
        fun key ->
            baselineTokens
            |> Map.tryFind key
            |> Option.map (fun token -> token &&& 0x00FFFFFF)

    let baselineTableRowCounts = request.Baseline.Metadata.TableRowCounts
    let baselinePropertyMapRowCount = baselineTableRowCounts.[int TableIndex.PropertyMap]
    let baselineEventMapRowCount = baselineTableRowCounts.[int TableIndex.EventMap]
    let lastMethodRowId = baselineTableRowCounts.[int TableIndex.MethodDef]
    let mutable nextTypeRefRowId = 0
    let mutable nextMemberRefRowId = 0
    let mutable nextAssemblyRefRowId = 0
    let typeReferenceRows = ResizeArray<TypeReferenceRowInfo>()
    let memberReferenceRows = ResizeArray<MemberReferenceRowInfo>()
    let assemblyReferenceRows = ResizeArray<AssemblyReferenceRowInfo>()
    let baselineTypeReferenceTokens = request.Baseline.TypeReferenceTokens
    let baselineAssemblyReferenceTokens = request.Baseline.AssemblyReferenceTokens
    if traceMetadata.Value then
        printfn
            "[fsharp-hotreload][metadata] baseline-typerefs=%d baseline-assemblyrefs=%d"
            (baselineTypeReferenceTokens |> Map.count)
            (baselineAssemblyReferenceTokens |> Map.count)
    let tryReuseBaselineTypeRef scopeName namespaceName typeName =
        let key =
            { TypeReferenceKey.Scope = scopeName
              Namespace = namespaceName
              Name = typeName }
        baselineTypeReferenceTokens |> Map.tryFind key

    let typeRefTokenMap = Dictionary<int, int>()
    let assemblyRefTokenMap = Dictionary<int, int>()
    let methodDefinitionIndex = DefinitionIndex<MethodDefinitionKey>(methodRowLookup, lastMethodRowId)
    let processedMethodKeys = HashSet<MethodDefinitionKey>()
    let addedMethodDeltaTokens = Dictionary<MethodDefinitionKey, int>(HashIdentity.Structural)

    for KeyValue(key, newToken) in addedMethodTokens do
        if not (methodDefinitionIndex.IsAdded key) then
            let rowId = methodDefinitionIndex.Add key
            let deltaToken = 0x06000000 ||| rowId
            addedMethodDeltaTokens[key] <- deltaToken
            addMapping methodTokenMap newToken deltaToken

    let inline remapWith (dict: Dictionary<int, int>) token =
        match dict.TryGetValue token with
        | true, mapped -> mapped
        | _ -> token

    let rec remapAssemblyRefToken token =
        match assemblyRefTokenMap.TryGetValue token with
        | true, mapped -> mapped
        | _ ->
            let handle = MetadataTokens.AssemblyReferenceHandle token
            let row = metadataReader.GetAssemblyReference handle
            let nextRowId = nextAssemblyRefRowId + 1
            nextAssemblyRefRowId <- nextRowId
            let getBlob (blob: BlobHandle) = if blob.IsNil then Array.empty else metadataReader.GetBlobBytes blob
            let info =
                { RowId = nextRowId
                  Version = row.Version
                  Flags = row.Flags
                  PublicKeyOrToken = getBlob row.PublicKeyOrToken
                  PublicKeyOrTokenHandle = None
                  Name = if row.Name.IsNil then "" else metadataReader.GetString row.Name
                  NameHandle = None
                  Culture = if row.Culture.IsNil then None else Some(metadataReader.GetString row.Culture)
                  CultureHandle = None
                  HashValue = getBlob row.HashValue
                  HashValueHandle = None }
            assemblyReferenceRows.Add info
            let deltaToken = 0x23000000 ||| nextRowId
            assemblyRefTokenMap[token] <- deltaToken
            deltaToken

    and remapTypeRefToken token =
        match typeRefTokenMap.TryGetValue token with
        | true, mapped -> mapped
        | _ ->
            let handle = MetadataTokens.TypeReferenceHandle token
            let row = metadataReader.GetTypeReference handle
            let name = if row.Name.IsNil then "" else metadataReader.GetString row.Name
            let namespaceName = if row.Namespace.IsNil then "" else metadataReader.GetString row.Namespace
            let baselineToken =
                match row.ResolutionScope.Kind with
                | HandleKind.AssemblyReference ->
                    let assemblyHandle = AssemblyReferenceHandle.op_Explicit row.ResolutionScope
                    let assemblyRef = metadataReader.GetAssemblyReference assemblyHandle
                    let scopeName = metadataReader.GetString assemblyRef.Name
                    tryReuseBaselineTypeRef scopeName namespaceName name
                | _ -> None
            match baselineToken with
            | Some reused ->
                typeRefTokenMap[token] <- reused
                reused
            | None ->
                if traceMetadata.Value then
                    printfn
                        "[fsharp-hotreload][metadata] remap typeref miss scope=%A ns=%s name=%s"
                        row.ResolutionScope.Kind
                        namespaceName
                        name
                let resolutionScope =
                    if row.ResolutionScope.IsNil then
                        struct (HandleKind.ModuleDefinition, 1)
                    else
                        let scopeToken = MetadataTokens.GetToken(row.ResolutionScope)
                        match row.ResolutionScope.Kind with
                        | HandleKind.AssemblyReference ->
                            let mapped = remapAssemblyRefToken scopeToken
                            struct (HandleKind.AssemblyReference, mapped &&& 0x00FFFFFF)
                        | HandleKind.TypeReference ->
                            let mapped = remapTypeRefToken scopeToken
                            struct (HandleKind.TypeReference, mapped &&& 0x00FFFFFF)
                        | HandleKind.ModuleDefinition ->
                            let rowId = MetadataTokens.GetRowNumber row.ResolutionScope
                            struct (HandleKind.ModuleDefinition, rowId)
                        | HandleKind.ModuleReference ->
                            let rowId = MetadataTokens.GetRowNumber row.ResolutionScope
                            struct (HandleKind.ModuleReference, rowId)
                        | _ -> struct (HandleKind.ModuleDefinition, 1)
                let nextRowId = nextTypeRefRowId + 1
                nextTypeRefRowId <- nextRowId
                typeReferenceRows.Add(
                    { RowId = nextRowId
                      ResolutionScope = resolutionScope
                      Name = name
                      NameHandle = None
                      Namespace = namespaceName
                      NamespaceHandle = None })
                let deltaToken = 0x01000000 ||| nextRowId
                typeRefTokenMap[token] <- deltaToken
                deltaToken
            

    and remapMemberRefToken token = token

    and remapEntityToken token =
        match token &&& 0xFF000000 with
        | 0x02000000 -> remapWith typeTokenMap token
        | 0x04000000 -> remapWith fieldTokenMap token
        | 0x06000000 -> remapWith methodTokenMap token
        | 0x0A000000 -> remapMemberRefToken token
        | 0x01000000 -> remapTypeRefToken token
        | 0x14000000 -> remapWith eventTokenMap token
        | 0x17000000 -> remapWith propertyTokenMap token
        | 0x23000000 -> remapAssemblyRefToken token
        | _ -> token

    let methodUpdateInputs =
        resolvedMethods
        |> List.choose (fun (_, _, _, key) ->
            match request.Baseline.MethodTokens |> Map.tryFind key with
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
                    Some(struct (key, methodToken, methodHandle, methodDef, body))
            | None ->
                match addedMethodTokens.TryGetValue key with
                | true, newMethodToken when addedMethodDeltaTokens.ContainsKey(key) ->
                    let methodHandle = MetadataTokens.MethodDefinitionHandle newMethodToken
                    if methodHandle.IsNil then
                        None
                    else
                        let methodDef = metadataReader.GetMethodDefinition methodHandle
                        let body = peReader.GetMethodBody(methodDef.RelativeVirtualAddress)
                        let deltaToken = addedMethodDeltaTokens[key]
                        if traceMethodUpdates.Value then
                            printfn
                                "[fsharp-hotreload][method-add] %s::%s token=0x%08X"
                                key.DeclaringType
                                key.Name
                                deltaToken
                        Some(struct (key, deltaToken, methodHandle, methodDef, body))
                | _ -> None)

    let parameterRowLookup = Dictionary<ParameterDefinitionKey, int>()
    let parameterHandleLookup = Dictionary<ParameterDefinitionKey, ParameterHandle>()
    let syntheticParameterInfo = Dictionary<ParameterDefinitionKey, ParameterAttributes>(HashIdentity.Structural)
    let returnParameterKeys = HashSet<ParameterDefinitionKey>(HashIdentity.Structural)
    let lastParamRowId = baselineTableRowCounts.[int TableIndex.Param]
    let parameterDefinitionIndex =
        let tryExisting key =
            match parameterRowLookup.TryGetValue key with
            | true, rowId -> Some rowId
            | _ -> None
        DefinitionIndex<ParameterDefinitionKey>(tryExisting, lastParamRowId)

    let propertyTokenToKey =
        let dict = Dictionary<int, PropertyDefinitionKey>()
        for KeyValue(key, token) in request.Baseline.PropertyTokens do
            dict[token] <- key
        dict

    for KeyValue(token, key) in addedPropertyTokenLookup do
        propertyTokenToKey[token] <- key

    let baselinePropertyLookup =
        let dict = Dictionary<string * string, PropertyDefinitionKey * int>()
        for KeyValue(key, token) in request.Baseline.PropertyTokens do
            dict.Add((key.DeclaringType, key.Name), (key, token &&& 0x00FFFFFF))
        dict

    let eventTokenToKey =
        let dict = Dictionary<int, EventDefinitionKey>()
        for KeyValue(key, token) in request.Baseline.EventTokens do
            dict[token] <- key
        dict

    for KeyValue(token, key) in addedEventTokenLookup do
        eventTokenToKey[token] <- key

    let baselineEventLookup =
        let dict = Dictionary<string * string, EventDefinitionKey * int>()
        for KeyValue(key, token) in request.Baseline.EventTokens do
            dict.Add((key.DeclaringType, key.Name), (key, token &&& 0x00FFFFFF))
        dict

    let methodTokenToKey =
        let dict = Dictionary<int, MethodDefinitionKey>()
        for KeyValue(key, token) in request.Baseline.MethodTokens do
            dict[token] <- key
        for KeyValue(key, token) in addedMethodDeltaTokens do
            dict[token] <- key
        dict

    let propertyRowLookup key =
        request.Baseline.PropertyTokens
        |> Map.tryFind key
        |> Option.map (fun token -> token &&& 0x00FFFFFF)

    let eventRowLookup key =
        request.Baseline.EventTokens
        |> Map.tryFind key
        |> Option.map (fun token -> token &&& 0x00FFFFFF)

    let lastPropertyRowId = baselineTableRowCounts.[int TableIndex.Property]
    let propertyDefinitionIndex = DefinitionIndex<PropertyDefinitionKey>(propertyRowLookup, lastPropertyRowId)
    let processedPropertyKeys = HashSet<PropertyDefinitionKey>()
    let addedPropertyDeltaTokens = Dictionary<PropertyDefinitionKey, int>(HashIdentity.Structural)

    for KeyValue(key, newToken) in addedPropertyTokens do
        if not (propertyDefinitionIndex.IsAdded key) then
            let rowId = propertyDefinitionIndex.Add key
            let deltaToken = 0x17000000 ||| rowId
            addedPropertyDeltaTokens[key] <- deltaToken
            addMapping propertyTokenMap newToken deltaToken

    for KeyValue(key, token) in addedPropertyDeltaTokens do
        propertyTokenToKey[token] <- key

    let lastEventRowId = baselineTableRowCounts.[int TableIndex.Event]
    let eventDefinitionIndex = DefinitionIndex<EventDefinitionKey>(eventRowLookup, lastEventRowId)
    let processedEventKeys = HashSet<EventDefinitionKey>()

    let addedEventDeltaTokens = Dictionary<EventDefinitionKey, int>(HashIdentity.Structural)

    for KeyValue(key, newToken) in addedEventTokens do
        if not (eventDefinitionIndex.IsAdded key) then
            let rowId = eventDefinitionIndex.Add key
            let deltaToken = 0x14000000 ||| rowId
            addedEventDeltaTokens[key] <- deltaToken
            addMapping eventTokenMap newToken deltaToken

    for KeyValue(key, token) in addedEventDeltaTokens do
        eventTokenToKey[token] <- key

    for struct (key, _, _, _, _) in methodUpdateInputs do
        if processedMethodKeys.Add key then
            if methodDefinitionIndex.IsAdded key then
                ()
            else
                methodDefinitionIndex.AddExisting key

    let methodUpdateLookup =
        let dict = Dictionary<MethodDefinitionKey, struct (MethodDefinitionKey * int * MethodDefinitionHandle * MethodDefinition * MethodBodyBlock)>()
        for struct (key, methodToken, methodHandle, methodDef, body) in methodUpdateInputs do
            dict[key] <- struct (key, methodToken, methodHandle, methodDef, body)
        dict

    let propertyAccessorLookup = Dictionary<MethodDefinitionHandle, PropertyDefinitionHandle>()
    for propertyHandle in metadataReader.PropertyDefinitions do
        let propertyDef = metadataReader.GetPropertyDefinition propertyHandle
        let accessors = propertyDef.GetAccessors()
        let getter = accessors.Getter
        if not getter.IsNil then
            propertyAccessorLookup[getter] <- propertyHandle
        let setter = accessors.Setter
        if not setter.IsNil then
            propertyAccessorLookup[setter] <- propertyHandle
        for otherHandle in accessors.Others do
            if not otherHandle.IsNil then
                propertyAccessorLookup[otherHandle] <- propertyHandle

    let eventAccessorLookup = Dictionary<MethodDefinitionHandle, EventDefinitionHandle>()
    for eventHandle in metadataReader.EventDefinitions do
        let eventDef = metadataReader.GetEventDefinition eventHandle
        let accessors = eventDef.GetAccessors()
        let adder = accessors.Adder
        if not adder.IsNil then
            eventAccessorLookup[adder] <- eventHandle
        let remover = accessors.Remover
        if not remover.IsNil then
            eventAccessorLookup[remover] <- eventHandle
        let raiser = accessors.Raiser
        if not raiser.IsNil then
            eventAccessorLookup[raiser] <- eventHandle

    let remapToken (map: Dictionary<int, int>) token =
        match map.TryGetValue token with
        | true, mapped -> mapped
        | _ -> token

    let methodDefinitionRowsRaw = methodDefinitionIndex.Rows

    let orderedMethodInputs =
        methodDefinitionRowsRaw
        |> List.choose (fun struct (_, key, _) ->
            match methodUpdateLookup.TryGetValue key with
            | true, data -> Some data
            | _ -> None)

    let baselineMethodHandles = request.Baseline.MetadataHandles.MethodHandles
    let baselineParameterHandles = request.Baseline.MetadataHandles.ParameterHandles
    let baselineParametersByMethod =
        let dict = Dictionary<MethodDefinitionKey, (ParameterDefinitionKey * ParameterDefinitionMetadataHandles) list>(HashIdentity.Structural)
        for KeyValue(paramKey, info) in baselineParameterHandles do
            let methodKey = paramKey.Method
            let existing =
                match dict.TryGetValue methodKey with
                | true, entries -> entries
                | _ -> []
            dict[methodKey] <- (paramKey, info) :: existing
        dict
    if traceMethodUpdates.Value then
        for KeyValue(methodKey, entries) in baselineParametersByMethod do
            printfn "[fsharp-hotreload][param-baseline] method=%s::%s entries=%d" methodKey.DeclaringType methodKey.Name (List.length entries)
    let baselinePropertyHandles = request.Baseline.MetadataHandles.PropertyHandles
    let baselineEventHandles = request.Baseline.MetadataHandles.EventHandles
    let firstParamRowByMethod = Dictionary<MethodDefinitionKey, int>(HashIdentity.Structural)

    let addSyntheticParameter key sequence attrs =
        let paramKey =
            { ParameterDefinitionKey.Method = key
              SequenceNumber = sequence }
        if not (parameterRowLookup.ContainsKey paramKey) then
            let rowId = parameterDefinitionIndex.Add paramKey
            parameterRowLookup[paramKey] <- rowId
            syntheticParameterInfo[paramKey] <- attrs
            rowId
        else
            parameterRowLookup[paramKey]

    let ensureReturnParameterRow key =
        let paramKey =
            { ParameterDefinitionKey.Method = key
              SequenceNumber = 0 }
        if parameterRowLookup.ContainsKey paramKey then
            parameterRowLookup[paramKey]
        else
            match baselineParameterHandles |> Map.tryFind paramKey |> Option.bind (fun info -> info.RowId) with
            | Some baselineRow when baselineRow > 0 ->
                parameterRowLookup[paramKey] <- baselineRow
                parameterDefinitionIndex.AddExisting paramKey
                baselineRow
            | _ ->
                let rowId = addSyntheticParameter key 0 ParameterAttributes.None
                returnParameterKeys.Add paramKey |> ignore
                rowId

    let enqueueParameters key methodHandle =
        let methodDef = metadataReader.GetMethodDefinition methodHandle
        let parameters = methodDef.GetParameters()
        let mutable sawParameter = false
        for parameterHandle in parameters do
            sawParameter <- true
            let parameter = metadataReader.GetParameter parameterHandle
            let paramKey =
                { ParameterDefinitionKey.Method = key
                  SequenceNumber = int parameter.SequenceNumber }
            if methodDefinitionIndex.IsAdded key then
                if not (parameterRowLookup.ContainsKey paramKey) then
                    let rowId = parameterDefinitionIndex.Add paramKey
                    parameterRowLookup[paramKey] <- rowId
                    parameterHandleLookup[paramKey] <- parameterHandle
            else
                if not (parameterRowLookup.ContainsKey paramKey) then
                    let rowId =
                        match baselineParameterHandles |> Map.tryFind paramKey |> Option.bind (fun info -> info.RowId) with
                        | Some baselineRow when baselineRow > 0 -> baselineRow
                        | _ -> MetadataTokens.GetRowNumber parameterHandle
                    parameterRowLookup[paramKey] <- rowId
                    parameterHandleLookup[paramKey] <- parameterHandle
                    parameterDefinitionIndex.AddExisting paramKey
        if not sawParameter then
            match baselineParametersByMethod.TryGetValue key with
            | true, entries when not (List.isEmpty entries) ->
                if traceMethodUpdates.Value then
                    printfn "[fsharp-hotreload][param-fallback] method=%s::%s entries=%d" key.DeclaringType key.Name (List.length entries)
                for (paramKey, info) in entries do
                    if not (parameterRowLookup.ContainsKey paramKey) then
                        match info.RowId with
                        | Some rowId when rowId > 0 ->
                            parameterRowLookup[paramKey] <- rowId
                            parameterDefinitionIndex.AddExisting paramKey
                        | _ ->
                            let syntheticRow = addSyntheticParameter key paramKey.SequenceNumber ParameterAttributes.None
                            if traceMethodUpdates.Value then
                                printfn "[fsharp-hotreload][param-fallback] synthesized baseline entry method=%s::%s seq=%d row=%d" key.DeclaringType key.Name paramKey.SequenceNumber syntheticRow
            | _ -> ()

        let _ = ensureReturnParameterRow key
        ()

    orderedMethodInputs
    |> List.iter (fun struct (key, _, methodHandle, _, _) -> enqueueParameters key methodHandle)

    let registerPropertyDefinition key handle =
        if processedPropertyKeys.Add key then
            if propertyDefinitionIndex.IsAdded key then
                ()
            else
                propertyDefinitionIndex.AddExisting key
        propertyHandleLookup[key] <- handle

    let registerEventDefinition key handle =
        if processedEventKeys.Add key then
            if eventDefinitionIndex.IsAdded key then
                ()
            else
                eventDefinitionIndex.AddExisting key
        eventHandleLookup[key] <- handle

    let tryGetMethodToken key =
        match request.Baseline.MethodTokens |> Map.tryFind key with
        | Some token -> Some token
        | None ->
            match addedMethodDeltaTokens.TryGetValue key with
            | true, token -> Some token
            | _ -> None

    let tryResolveAccessor methodToken =
        let methodHandle = MetadataTokens.MethodDefinitionHandle methodToken
        match propertyAccessorLookup.TryGetValue methodHandle with
        | true, propertyHandle ->
            if traceMethodUpdates.Value then
                printfn "[fsharp-hotreload][accessor] property handle matched token=0x%08X" (MetadataTokens.GetToken(EntityHandle.op_Implicit propertyHandle))
            let associationToken = MetadataTokens.GetToken(EntityHandle.op_Implicit propertyHandle)
            let baselineToken = remapToken propertyTokenMap associationToken
            match propertyTokenToKey.TryGetValue(baselineToken) with
            | true, key ->
                let baselineHandle = MetadataTokens.PropertyDefinitionHandle baselineToken
                registerPropertyDefinition key baselineHandle
            | _ -> ()
        | _ ->
            if traceMethodUpdates.Value then
                printfn "[fsharp-hotreload][accessor] property handle missing for method token=0x%08X" (MetadataTokens.GetToken(EntityHandle.op_Implicit methodHandle))
            match eventAccessorLookup.TryGetValue methodHandle with
            | true, eventHandle ->
                let associationToken = MetadataTokens.GetToken(EntityHandle.op_Implicit eventHandle)
                let baselineToken = remapToken eventTokenMap associationToken
                match eventTokenToKey.TryGetValue(baselineToken) with
                | true, key ->
                    let baselineHandle = MetadataTokens.EventDefinitionHandle baselineToken
                    registerEventDefinition key baselineHandle
                | _ -> ()
            | _ -> ()

    for accessor in request.UpdatedAccessors do
        match accessor.Method with
        | Some methodKey ->
            match tryGetMethodToken methodKey with
            | Some methodToken -> tryResolveAccessor methodToken
            | None -> ()
        | None -> ()

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

    let updatedMethodTokenList =
        orderedMethodInputs
        |> List.map (fun struct (_, methodToken, _, _, _) -> methodToken)

    if List.isEmpty methodUpdateInputs && List.isEmpty updatedTypeTokens then
        emptyDelta
    else
        let methodUpdatesWithDefs =
            orderedMethodInputs
            |> List.map (fun struct (key, methodToken, methodHandle, methodDef, body) ->
                let ilBytes = rewriteMethodBody remapUserString remapEntityToken body
                let localSigToken =
                    if body.LocalSignature.IsNil then
                        0
                    else
                        let standalone = metadataReader.GetStandaloneSignature body.LocalSignature
                        let signatureBytes = metadataReader.GetBlobBytes standalone.Signature
                        builder.AddStandaloneSignature(signatureBytes)

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

                ({ MethodKey = key
                   MethodToken = methodToken
                   MethodHandle = methodHandle
                   Body = bodyUpdate }, methodDef))

        let methodMetadataLookup =
            let dict : Dictionary<MethodDefinitionKey, struct (MethodAttributes * MethodImplAttributes * string * byte[] * StringHandle option * BlobHandle option)> =
                Dictionary(HashIdentity.Structural)
            for update, methodDef in methodUpdatesWithDefs do
                let name = metadataReader.GetString methodDef.Name
                let signature = metadataReader.GetBlobBytes methodDef.Signature
                let nameHandle = if methodDef.Name.IsNil then None else Some methodDef.Name
                let signatureHandle = if methodDef.Signature.IsNil then None else Some methodDef.Signature
                dict[update.MethodKey] <-
                    struct (methodDef.Attributes, methodDef.ImplAttributes, name, signature, nameHandle, signatureHandle)
            dict

        let parameterDefinitionRowsSnapshot =
            parameterDefinitionIndex.Rows
            |> List.choose (fun struct (rowId, key, isAdded) ->
                if rowId = 0 then
                    None
                else
                    let attrs, sequence, nameOpt, resolvedHandleOpt =
                        match parameterHandleLookup.TryGetValue key with
                        | true, handle when not handle.IsNil ->
                            let parameter = metadataReader.GetParameter handle
                            let name =
                                if parameter.Name.IsNil then
                                    None
                                else
                                    metadataReader.GetString parameter.Name |> Some
                            let resolvedHandle =
                                match baselineParameterHandles |> Map.tryFind key |> Option.bind (fun info -> info.NameHandle) with
                                | Some handle -> Some handle
                                | None -> if parameter.Name.IsNil then None else Some parameter.Name
                            parameter.Attributes, int parameter.SequenceNumber, name, resolvedHandle
                        | _ ->
                            let attrs =
                                match syntheticParameterInfo.TryGetValue key with
                                | true, value -> value
                                | _ -> ParameterAttributes.None
                            attrs, key.SequenceNumber, None, None

                    match firstParamRowByMethod.TryGetValue key.Method with
                    | true, existing when existing <= rowId -> ()
                    | _ -> firstParamRowByMethod[key.Method] <- rowId

                    // Treat synthesized return parameter rows as added so EncLog/EncMap
                    // reflect the new Param table entry, mirroring Roslyn ENC behavior.
                    let effectiveIsAdded =
                        if returnParameterKeys.Contains key then true else isAdded
                    Some
                        { ParameterDefinitionRowInfo.Key = key
                          RowId = rowId
                          IsAdded = effectiveIsAdded
                          Attributes = attrs
                          SequenceNumber = sequence
                          Name = nameOpt
                          NameHandle = resolvedHandleOpt })
        if traceMethodUpdates.Value then
            printfn "[fsharp-hotreload][param-rows] count=%d" parameterDefinitionRowsSnapshot.Length

        let tryBuildMethodRow rowId key isAdded =
                match methodMetadataLookup.TryGetValue key with
                | true, struct (attrs, implAttrs, name, signature, emittedNameHandle, emittedSignatureHandle) ->
                    let baselineHandles = baselineMethodHandles |> Map.tryFind key
                    let resolvedNameHandle =
                        match baselineHandles |> Option.bind (fun info -> info.NameHandle) with
                        | Some handle -> Some handle
                        | None -> emittedNameHandle
                    let resolvedSignatureHandle =
                        match baselineHandles |> Option.bind (fun info -> info.SignatureHandle) with
                        | Some handle -> Some handle
                        | None -> emittedSignatureHandle
                    let resolvedAttributes =
                        match baselineHandles |> Option.bind (fun info -> info.Attributes) with
                        | Some value -> value
                        | None -> attrs
                    let resolvedImplAttributes =
                        match baselineHandles |> Option.bind (fun info -> info.ImplAttributes) with
                        | Some value -> value
                        | None -> implAttrs
                    let resolvedCodeRva = baselineHandles |> Option.bind (fun info -> info.Rva)
                    let baselineFirstParam =
                        baselineHandles
                        |> Option.bind (fun info -> info.FirstParameterRowId)

                    let firstParam =
                        match firstParamRowByMethod.TryGetValue key with
                        | true, value when value > 0 -> Some value
                        | _ ->
                            match baselineFirstParam with
                            | Some _ as baselineRow -> baselineRow
                            | None -> None
                    Some
                        { MethodDefinitionRowInfo.Key = key
                          RowId = rowId
                          IsAdded = isAdded
                          Attributes = resolvedAttributes
                          ImplAttributes = resolvedImplAttributes
                          Name = name
                          NameHandle = resolvedNameHandle
                          Signature = signature
                          SignatureHandle = resolvedSignatureHandle
                          FirstParameterRowId = firstParam
                          CodeRva = resolvedCodeRva }
                | _ -> None

        let methodDefinitionRowsSnapshot =
            let initialRows =
                methodDefinitionRowsRaw
                |> List.choose (fun struct (rowId, key, isAdded) -> tryBuildMethodRow rowId key isAdded)

            let existingKeys = HashSet<MethodDefinitionKey>(initialRows |> Seq.map (fun row -> row.Key), HashIdentity.Structural)

            let missingRows =
                methodUpdatesWithDefs
                |> List.choose (fun (update, _) ->
                    if existingKeys.Contains update.MethodKey then
                        None
                    else
                        let rowId =
                            match request.Baseline.MethodTokens |> Map.tryFind update.MethodKey with
                            | Some token -> token &&& 0x00FFFFFF
                            | None -> methodDefinitionIndex.GetRowId update.MethodKey

                        tryBuildMethodRow rowId update.MethodKey false)

            let rows = initialRows @ missingRows

            if traceMethodUpdates.Value then
                printfn "[fsharp-hotreload][method-rows] count=%d (missing=%d)" rows.Length missingRows.Length

            rows

        let propertyDefinitionRowsSnapshot =
            propertyDefinitionIndex.Rows
            |> List.choose (fun struct (rowId, key, isAdded) ->
                match propertyHandleLookup.TryGetValue key with
                | true, handle when not handle.IsNil ->
                    let propertyDef = metadataReader.GetPropertyDefinition handle
                    let name = metadataReader.GetString propertyDef.Name
                    let signature = metadataReader.GetBlobBytes propertyDef.Signature
                    let baselineHandles = baselinePropertyHandles |> Map.tryFind key
                    let resolvedNameHandle =
                        match baselineHandles |> Option.bind (fun info -> info.NameHandle) with
                        | Some handle -> Some handle
                        | None -> if propertyDef.Name.IsNil then None else Some propertyDef.Name
                    let resolvedSignatureHandle =
                        match baselineHandles |> Option.bind (fun info -> info.SignatureHandle) with
                        | Some handle -> Some handle
                        | None -> if propertyDef.Signature.IsNil then None else Some propertyDef.Signature
                    Some
                        { PropertyDefinitionRowInfo.Key = key
                          RowId = rowId
                          IsAdded = isAdded
                          Name = name
                          NameHandle = resolvedNameHandle
                          Signature = signature
                          SignatureHandle = resolvedSignatureHandle
                          Attributes = propertyDef.Attributes }
                | _ -> None)

        if traceMethodUpdates.Value then
            printfn "[fsharp-hotreload][property-rows] count=%d" propertyDefinitionRowsSnapshot.Length

        let eventDefinitionRowsSnapshot =
            eventDefinitionIndex.Rows
            |> List.choose (fun struct (rowId, key, isAdded) ->
                match eventHandleLookup.TryGetValue key with
                | true, handle when not handle.IsNil ->
                    let eventDef = metadataReader.GetEventDefinition handle
                    let name = metadataReader.GetString eventDef.Name
                    let resolvedNameHandle =
                        match baselineEventHandles |> Map.tryFind key |> Option.bind (fun info -> info.NameHandle) with
                        | Some handle -> Some handle
                        | None -> if eventDef.Name.IsNil then None else Some eventDef.Name
                    Some
                        { EventDefinitionRowInfo.Key = key
                          RowId = rowId
                          IsAdded = isAdded
                          Name = name
                          NameHandle = resolvedNameHandle
                          Attributes = eventDef.Attributes
                          EventType = eventDef.Type }
                | _ -> None)

        let propertyRowsByType =
            propertyDefinitionRowsSnapshot
            |> List.groupBy (fun row -> row.Key.DeclaringType)
            |> dict

        let propertyRowsByName =
            propertyDefinitionRowsSnapshot
            |> List.groupBy (fun row -> struct (row.Key.DeclaringType, row.Key.Name))
            |> dict

        let eventRowsByType =
            eventDefinitionRowsSnapshot
            |> List.groupBy (fun row -> row.Key.DeclaringType)
            |> dict

        let eventRowsByName =
            eventDefinitionRowsSnapshot
            |> List.groupBy (fun row -> struct (row.Key.DeclaringType, row.Key.Name))
            |> dict

        let propertyMapDefinitionIndex =
            let tryExisting typeName = request.Baseline.PropertyMapEntries |> Map.tryFind typeName
            DefinitionIndex<string>(tryExisting, baselinePropertyMapRowCount)

        let eventMapDefinitionIndex =
            let tryExisting typeName = request.Baseline.EventMapEntries |> Map.tryFind typeName
            DefinitionIndex<string>(tryExisting, baselineEventMapRowCount)

        let propertyMapRowsSnapshot =
            let missingTypes =
                propertyRowsByType.Keys
                |> Seq.filter (fun typeName -> not (request.Baseline.PropertyMapEntries |> Map.containsKey typeName))
                |> Seq.toList

            for typeName in missingTypes do
                propertyMapDefinitionIndex.Add typeName |> ignore

            propertyMapDefinitionIndex.Rows
            |> List.choose (fun struct (rowId, typeName, isAdded) ->
                let typeTokenOpt = request.Baseline.TypeTokens |> Map.tryFind typeName
                let firstPropertyRowIdOpt =
                    match propertyRowsByType.TryGetValue typeName with
                    | true, rows ->
                        rows
                        |> List.sortBy (fun row -> row.RowId)
                        |> List.tryHead
                        |> Option.map (fun row -> row.RowId)
                    | _ -> None

                let shouldAdd = isAdded || List.contains typeName missingTypes

                match typeTokenOpt, firstPropertyRowIdOpt, shouldAdd with
                | Some typeToken, Some firstRowId, true ->
                    Some
                        { PropertyMapRowInfo.DeclaringType = typeName
                          RowId = rowId
                          TypeDefRowId = typeToken &&& 0x00FFFFFF
                          FirstPropertyRowId = Some firstRowId
                          IsAdded = true }
                | _ -> None)

        let eventMapRowsSnapshot =
            let missingTypes =
                eventRowsByType.Keys
                |> Seq.filter (fun typeName -> not (request.Baseline.EventMapEntries |> Map.containsKey typeName))
                |> Seq.toList

            for typeName in missingTypes do
                eventMapDefinitionIndex.Add typeName |> ignore

            eventMapDefinitionIndex.Rows
            |> List.choose (fun struct (rowId, typeName, isAdded) ->
                let typeTokenOpt = request.Baseline.TypeTokens |> Map.tryFind typeName
                let firstEventRowIdOpt =
                    match eventRowsByType.TryGetValue typeName with
                    | true, rows ->
                        rows
                        |> List.sortBy (fun row -> row.RowId)
                        |> List.tryHead
                        |> Option.map (fun row -> row.RowId)
                    | _ -> None

                let shouldAdd = isAdded || List.contains typeName missingTypes

                match typeTokenOpt, firstEventRowIdOpt, shouldAdd with
                | Some typeToken, Some firstRowId, true ->
                    Some
                        { EventMapRowInfo.DeclaringType = typeName
                          RowId = rowId
                          TypeDefRowId = typeToken &&& 0x00FFFFFF
                          FirstEventRowId = Some firstRowId
                          IsAdded = true }
                | _ -> None)

        let missingPropertyMapTypes =
            propertyMapRowsSnapshot
            |> List.filter (fun row -> row.IsAdded)
            |> List.map (fun row -> row.DeclaringType)
            |> HashSet

        let missingEventMapTypes =
            eventMapRowsSnapshot
            |> List.filter (fun row -> row.IsAdded)
            |> List.map (fun row -> row.DeclaringType)
            |> HashSet

        let tryGetPropertyAssociation typeName propertyName =
            match propertyRowsByName.TryGetValue(struct (typeName, propertyName)) with
            | true, rows ->
                rows
                |> List.sortBy (fun row -> row.RowId)
                |> List.tryHead
                |> Option.map (fun row -> (row.RowId, row.Key))
            | _ ->
                match baselinePropertyLookup.TryGetValue((typeName, propertyName)) with
                | true, (key, rowId) -> Some(rowId, key)
                | _ -> None

        let tryGetEventAssociation typeName eventName =
            match eventRowsByName.TryGetValue(struct (typeName, eventName)) with
            | true, rows ->
                rows
                |> List.sortBy (fun row -> row.RowId)
                |> List.tryHead
                |> Option.map (fun row -> (row.RowId, row.Key))
            | _ ->
                match baselineEventLookup.TryGetValue((typeName, eventName)) with
                | true, (key, rowId) -> Some(rowId, key)
                | _ -> None

        let semanticsAttributeForMemberKind memberKind =
            match memberKind with
            | SymbolMemberKind.PropertyGet _ -> MethodSemanticsAttributes.Getter
            | SymbolMemberKind.PropertySet _ -> MethodSemanticsAttributes.Setter
            | SymbolMemberKind.EventAdd _ -> MethodSemanticsAttributes.Adder
            | SymbolMemberKind.EventRemove _ -> MethodSemanticsAttributes.Remover
            | SymbolMemberKind.EventInvoke _ -> MethodSemanticsAttributes.Raiser
            | _ -> MethodSemanticsAttributes.Other

        let accessorName memberKind =
            match memberKind with
            | SymbolMemberKind.PropertyGet name
            | SymbolMemberKind.PropertySet name -> Some name
            | SymbolMemberKind.EventAdd name
            | SymbolMemberKind.EventRemove name
            | SymbolMemberKind.EventInvoke name -> Some name
            | _ -> None

        let mutable nextMethodSemanticsRowId = baselineTableRowCounts.[int TableIndex.MethodSemantics]

        let methodSemanticsRowsSnapshot =
            request.UpdatedAccessors
            |> List.choose (fun accessor ->
                match accessor.Method with
                | None -> None
                | Some methodKey ->
                    match tryGetMethodToken methodKey with
                    | None -> None
                    | Some methodToken ->
                        let typeName = accessor.ContainingType
                        let attrs = semanticsAttributeForMemberKind accessor.MemberKind
                        match accessor.MemberKind, accessorName accessor.MemberKind with
                        | (SymbolMemberKind.PropertyGet _
                          | SymbolMemberKind.PropertySet _), Some propertyName when missingPropertyMapTypes.Contains typeName ->
                            match tryGetPropertyAssociation typeName propertyName with
                            | Some(propertyRowId, propertyKey) ->
                                nextMethodSemanticsRowId <- nextMethodSemanticsRowId + 1
                                Some
                                    { MethodSemanticsMetadataUpdate.RowId = nextMethodSemanticsRowId
                                      Association =
                                        MetadataTokens.PropertyDefinitionHandle propertyRowId
                                        |> PropertyDefinitionHandle.op_Implicit
                                      MethodToken = methodToken
                                      Attributes = attrs
                                      IsAdded = true
                                      AssociationInfo =
                                        MethodSemanticsAssociation.PropertyAssociation(propertyKey, propertyRowId)
                                        |> Some }
                            | None -> None
                        | (SymbolMemberKind.EventAdd _
                          | SymbolMemberKind.EventRemove _
                          | SymbolMemberKind.EventInvoke _), Some eventName when missingEventMapTypes.Contains typeName ->
                            match tryGetEventAssociation typeName eventName with
                            | Some(eventRowId, eventKey) ->
                                nextMethodSemanticsRowId <- nextMethodSemanticsRowId + 1
                                Some
                                    { MethodSemanticsMetadataUpdate.RowId = nextMethodSemanticsRowId
                                      Association =
                                        MetadataTokens.EventDefinitionHandle eventRowId
                                        |> EventDefinitionHandle.op_Implicit
                                      MethodToken = methodToken
                                      Attributes = attrs
                                      IsAdded = true
                                      AssociationInfo =
                                        MethodSemanticsAssociation.EventAssociation(eventKey, eventRowId)
                                        |> Some }
                            | None -> None
                        | _ -> None)

        let methodUpdates = methodUpdatesWithDefs |> List.map fst

        let baselineHeapOffsets =
            request.Baseline.Metadata.HeapSizes
            |> MetadataHeapOffsets.OfHeapSizes

        let userStringEntries =
            userStringUpdates
            |> Seq.toList

        let customAttributeRowList : CustomAttributeRowInfo list =
            let rows = ResizeArray<CustomAttributeRowInfo>()
            let mutable nextRowId = baselineTableRowCounts.[int TableIndex.CustomAttribute]
            let methodRowIdToKey = Dictionary<int, MethodDefinitionKey>(HashIdentity.Structural)
            for struct (rowId, key, _) in methodDefinitionIndex.Rows do
                methodRowIdToKey[rowId] <- key

            let methodsWithCustomAttribute = HashSet<MethodDefinitionKey>(HashIdentity.Structural)
            let methodsWithNullableContextAttribute = HashSet<MethodDefinitionKey>(HashIdentity.Structural)
            let mutable nullableContextAttributeSeen = false
            let encodeNullableContextValue () =
                [| 0x01uy; 0x00uy; 0x01uy; 0x00uy; 0x00uy |]

            let rec getFullTypeName (handle: TypeDefinitionHandle) =
                let def = metadataReader.GetTypeDefinition handle
                let name = metadataReader.GetString def.Name
                let ns =
                    if def.Namespace.IsNil then
                        ""
                    else
                        metadataReader.GetString def.Namespace
                let declaring = def.GetDeclaringType()
                if declaring.IsNil then
                    if String.IsNullOrEmpty ns then
                        name
                    else
                        ns + "." + name
                else
                    getFullTypeName declaring + "+" + name

            let tryFindTypeDefinition fullName =
                metadataReader.TypeDefinitions
                |> Seq.tryPick (fun handle ->
                    let def = metadataReader.GetTypeDefinition handle
                    let name = metadataReader.GetString def.Name
                    let ns =
                        if def.Namespace.IsNil then
                            ""
                        else
                            metadataReader.GetString def.Namespace
                    let decl =
                        if String.IsNullOrEmpty ns then
                            name
                        else
                            ns + "." + name
                    if String.Equals(decl, fullName, StringComparison.Ordinal) then
                        Some handle
                    else
                        None)

            let tryFindStateMachineType (methodKey: MethodDefinitionKey) =
                match tryFindTypeDefinition methodKey.DeclaringType with
                | None -> ValueNone
                | Some parentHandle ->
                    let parentDef = metadataReader.GetTypeDefinition parentHandle
                    let nestedTypes = parentDef.GetNestedTypes()
                    let prefix = methodKey.Name + "@hotreload"
                    let matches =
                        nestedTypes
                        |> Seq.choose (fun nested ->
                            let nestedDef = metadataReader.GetTypeDefinition nested
                            let name = metadataReader.GetString nestedDef.Name
                            if name.StartsWith(prefix, StringComparison.Ordinal) then
                                Some(name, nested)
                            else
                                None)
                        |> Seq.toArray

                    if matches.Length = 0 then
                        ValueNone
                    else
                        matches
                        |> Array.tryFind (fun (name, _) -> String.Equals(name, prefix, StringComparison.Ordinal))
                        |> Option.orElseWith (fun () -> matches |> Array.tryHead)
                        |> Option.map snd
                        |> ValueOption.ofOption

            let findAssemblyReferenceRow scopeName =
                metadataReader.AssemblyReferences
                |> Seq.tryPick (fun handle ->
                    let reference = metadataReader.GetAssemblyReference handle
                    let name = metadataReader.GetString reference.Name
                    if String.Equals(name, scopeName, StringComparison.OrdinalIgnoreCase) then
                        let token = MetadataTokens.GetToken(EntityHandle.op_Implicit handle)
                        let remapped = remapAssemblyRefToken token
                        let rowId = remapped &&& 0x00FFFFFF
                        Some(struct (HandleKind.AssemblyReference, rowId))
                    else
                        None)

            let tryGetAssemblyScope () =
                match findAssemblyReferenceRow "System.Runtime" with
                | Some scope -> Some scope
                | None -> findAssemblyReferenceRow "mscorlib"

            let tryFindSystemTypeRef () =
                metadataReader.TypeReferences
                |> Seq.tryPick (fun handle ->
                    let typeRef = metadataReader.GetTypeReference handle
                    let name = metadataReader.GetString typeRef.Name
                    let ns =
                        if typeRef.Namespace.IsNil then
                            ""
                        else
                            metadataReader.GetString typeRef.Namespace
                    if String.Equals(name, "Type", StringComparison.Ordinal)
                       && String.Equals(ns, "System", StringComparison.Ordinal) then
                        Some handle
                    else
                        None)

            let tryReuseBaselineTypeRef scopeName namespaceName typeName =
                let key =
                    { TypeReferenceKey.Scope = scopeName
                      Namespace = namespaceName
                      Name = typeName }
                baselineTypeReferenceTokens |> Map.tryFind key

            let tryFindExistingTypeRef scopeName namespaceName typeName =
                metadataReader.TypeReferences
                |> Seq.tryPick (fun handle ->
                    let typeRef = metadataReader.GetTypeReference handle
                    let name = metadataReader.GetString typeRef.Name
                    if name = typeName then
                        let ns = if typeRef.Namespace.IsNil then "" else metadataReader.GetString typeRef.Namespace
                        if ns = namespaceName then
                            match typeRef.ResolutionScope.Kind with
                            | HandleKind.AssemblyReference ->
                                let asm =
                                    metadataReader.GetAssemblyReference(
                                        AssemblyReferenceHandle.op_Explicit typeRef.ResolutionScope
                                    )
                                let asmName = metadataReader.GetString asm.Name
                                if asmName = scopeName then
                                    Some handle
                                else
                                    None
                            | _ -> None
                        else
                            None
                    else
                        None)

            let mutable systemObjectTypeRefToken : int option = None
            let mutable asyncAttributeTypeRefToken : int option = None
            let mutable asyncAttributeCtorToken : int option = None
            let mutable nullableContextAttributeTypeRefToken : int option = None
            let mutable nullableContextAttributeCtorToken : int option = None

            let ensureSystemObjectTypeRef () =
                match systemObjectTypeRefToken with
                | Some token -> token
                | None ->
                    let scope =
                        match tryGetAssemblyScope () with
                        | Some value -> value
                        | None -> struct (HandleKind.ModuleDefinition, 1)
                    let nextRowId = nextTypeRefRowId + 1
                    nextTypeRefRowId <- nextRowId
                    typeReferenceRows.Add(
                        { RowId = nextRowId
                          ResolutionScope = scope
                          Name = "Object"
                          NameHandle = None
                          Namespace = "System"
                          NamespaceHandle = None })
                    let token = 0x01000000 ||| nextRowId
                    systemObjectTypeRefToken <- Some token
                    token

            let ensureSystemTypeRefHandle () =
                match tryFindSystemTypeRef () with
                | Some handle -> handle
                | None ->
                    let scope =
                        match tryGetAssemblyScope () with
                        | Some value -> value
                        | None -> struct (HandleKind.ModuleDefinition, 1)
                    let nextRowId = nextTypeRefRowId + 1
                    nextTypeRefRowId <- nextRowId
                    typeReferenceRows.Add(
                        { RowId = nextRowId
                          ResolutionScope = scope
                          Name = "Type"
                          NameHandle = None
                          Namespace = "System"
                          NamespaceHandle = None })
                    MetadataTokens.TypeReferenceHandle nextRowId

            let ensureAsyncAttributeTypeRef () =
                match asyncAttributeTypeRefToken with
                | Some token -> token
                | None ->
                    let scope =
                        match tryGetAssemblyScope () with
                        | Some value -> value
                        | None -> failwith "Unable to locate System.Runtime/mscorlib assembly reference for AsyncStateMachineAttribute."
                    let nextRowId = nextTypeRefRowId + 1
                    nextTypeRefRowId <- nextRowId
                    typeReferenceRows.Add(
                        { RowId = nextRowId
                          ResolutionScope = scope
                          Name = "AsyncStateMachineAttribute"
                          NameHandle = None
                          Namespace = "System.Runtime.CompilerServices"
                          NamespaceHandle = None })
                    let token = 0x01000000 ||| nextRowId
                    asyncAttributeTypeRefToken <- Some token
                    token

            let ensureAsyncAttributeCtor () =
                match asyncAttributeCtorToken with
                | Some token -> token
                | None ->
                    let attrTypeToken = ensureAsyncAttributeTypeRef ()
                    let systemTypeRefHandle = ensureSystemTypeRefHandle ()

                    let signatureBytes =
                        let blob = BlobBuilder()
                        let instanceHeader =
                            (int SignatureCallingConvention.Default)
                            ||| (int SignatureAttributes.Instance)
                            |> byte
                        blob.WriteByte(instanceHeader)
                        blob.WriteCompressedInteger 1
                        blob.WriteByte(LanguagePrimitives.EnumToValue SignatureTypeCode.Void)
                        blob.WriteByte(0x12uy)
                        let typeRefEntity : EntityHandle = TypeReferenceHandle.op_Implicit systemTypeRefHandle
                        let codedIndex = CodedIndex.TypeDefOrRef typeRefEntity
                        blob.WriteCompressedInteger codedIndex
                        blob.ToArray()

                    let parentRowId = attrTypeToken &&& 0x00FFFFFF
                    let nextRowId = nextMemberRefRowId + 1
                    nextMemberRefRowId <- nextRowId
                    memberReferenceRows.Add(
                        { RowId = nextRowId
                          Parent = struct (HandleKind.TypeReference, parentRowId)
                          Name = ".ctor"
                          NameHandle = None
                          Signature = signatureBytes
                          SignatureHandle = None })

                    let token = 0x0A000000 ||| nextRowId
                    asyncAttributeCtorToken <- Some token
                    token

            let ensureNullableContextAttributeTypeRef () =
                match nullableContextAttributeTypeRefToken with
                | Some token -> token
                | None ->
                    let baselineOrExistingToken =
                        [ "System.Runtime"; "mscorlib" ]
                        |> List.tryPick (fun scope ->
                            match tryReuseBaselineTypeRef scope "System.Runtime.CompilerServices" "NullableContextAttribute" with
                            | Some token -> Some token
                            | None ->
                                match tryFindExistingTypeRef scope "System.Runtime.CompilerServices" "NullableContextAttribute" with
                                | Some handle -> Some(MetadataTokens.GetToken(EntityHandle.op_Implicit handle))
                                | None -> None)
                    match baselineOrExistingToken with
                    | Some token ->
                        nullableContextAttributeTypeRefToken <- Some token
                        token
                    | None ->
                        let scope =
                            match tryGetAssemblyScope () with
                            | Some value -> value
                            | None -> failwith "Unable to locate System.Runtime/mscorlib assembly reference for NullableContextAttribute."
                        let nextRowId = nextTypeRefRowId + 1
                        nextTypeRefRowId <- nextRowId
                        typeReferenceRows.Add(
                            { RowId = nextRowId
                              ResolutionScope = scope
                              Name = "NullableContextAttribute"
                              NameHandle = None
                              Namespace = "System.Runtime.CompilerServices"
                              NamespaceHandle = None })
                        let token = 0x01000000 ||| nextRowId
                        nullableContextAttributeTypeRefToken <- Some token
                        let _ = ensureSystemObjectTypeRef ()
                        token

            let ensureNullableContextAttributeCtor () =
                match nullableContextAttributeCtorToken with
                | Some token -> token
                | None ->
                    let attrTypeToken = ensureNullableContextAttributeTypeRef ()
                    let signatureBytes =
                        let blob = BlobBuilder()
                        let instanceHeader =
                            (int SignatureCallingConvention.Default)
                            ||| (int SignatureAttributes.Instance)
                            |> byte
                        blob.WriteByte(instanceHeader)
                        blob.WriteCompressedInteger 1
                        blob.WriteByte(LanguagePrimitives.EnumToValue SignatureTypeCode.Void)
                        blob.WriteByte(LanguagePrimitives.EnumToValue SignatureTypeCode.Byte)
                        blob.ToArray()

                    let parentRowId = attrTypeToken &&& 0x00FFFFFF
                    let nextRowId = nextMemberRefRowId + 1
                    nextMemberRefRowId <- nextRowId
                    memberReferenceRows.Add(
                        { RowId = nextRowId
                          Parent = struct (HandleKind.TypeReference, parentRowId)
                          Name = ".ctor"
                          NameHandle = None
                          Signature = signatureBytes
                          SignatureHandle = None })

                    let token = 0x0A000000 ||| nextRowId
                    nullableContextAttributeCtorToken <- Some token
                    token


            let encodeAsyncAttributeValue (stateMachineFullName: string) =
                let blob = BlobBuilder()
                blob.WriteUInt16(0x0001us)
                blob.WriteSerializedString(stateMachineFullName)
                blob.WriteUInt16(0us)
                blob.ToArray()

            let isAsyncStateMachineAttribute (attribute: CustomAttribute) =
                match attribute.Constructor.Kind with
                | HandleKind.MemberReference ->
                    let memberRef = metadataReader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)
                    match memberRef.Parent.Kind with
                    | HandleKind.TypeReference ->
                        let typeRef = metadataReader.GetTypeReference(TypeReferenceHandle.op_Explicit memberRef.Parent)
                        let name = metadataReader.GetString typeRef.Name
                        let ns = if typeRef.Namespace.IsNil then "" else metadataReader.GetString typeRef.Namespace
                        ns = "System.Runtime.CompilerServices"
                        && name.EndsWith("StateMachineAttribute", StringComparison.Ordinal)
                    | _ -> false
                | _ -> false

            let isNullableContextAttribute (attribute: CustomAttribute) =
                match attribute.Constructor.Kind with
                | HandleKind.MemberReference ->
                    let memberRef = metadataReader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)
                    match memberRef.Parent.Kind with
                    | HandleKind.TypeReference ->
                        let typeRef = metadataReader.GetTypeReference(TypeReferenceHandle.op_Explicit memberRef.Parent)
                        let name = metadataReader.GetString typeRef.Name
                        let ns = if typeRef.Namespace.IsNil then "" else metadataReader.GetString typeRef.Namespace
                        ns = "System.Runtime.CompilerServices"
                        && name = "NullableContextAttribute"
                    | _ -> false
                | _ -> false


            for struct (key: MethodDefinitionKey, _, _, methodDef, _) in methodUpdateInputs do
                if methodDefinitionIndex.Contains key then
                    let parentRowId = methodDefinitionIndex.GetRowId key
                    for attributeHandle in methodDef.GetCustomAttributes() do
                        let attribute = metadataReader.GetCustomAttribute attributeHandle
                        let constructorToken = MetadataTokens.GetToken attribute.Constructor
                        let remappedConstructorToken = remapEntityToken constructorToken
                        let ctorRowId = remappedConstructorToken &&& 0x00FFFFFF
                        nextRowId <- nextRowId + 1
                        let valueBytes =
                            if attribute.Value.IsNil then
                                Array.empty
                            else
                                metadataReader.GetBlobBytes attribute.Value

                        if isAsyncStateMachineAttribute attribute then
                            match methodRowIdToKey.TryGetValue parentRowId with
                            | true, methodKey -> methodsWithCustomAttribute.Add methodKey |> ignore
                            | _ -> ()
                        if isNullableContextAttribute attribute then
                            nullableContextAttributeSeen <- true
                            match methodRowIdToKey.TryGetValue parentRowId with
                            | true, methodKey ->
                                if traceMetadata.Value then
                                    printfn
                                        "[fsharp-hotreload][metadata] nullable-context attribute detected on %s"
                                        methodKey.Name
                                methodsWithNullableContextAttribute.Add methodKey |> ignore
                            | _ -> ()

                        rows.Add(
                            { RowId = nextRowId
                              Parent = struct (HandleKind.MethodDefinition, parentRowId)
                              Constructor = struct (attribute.Constructor.Kind, ctorRowId)
                              Value = valueBytes
                              ValueHandle = if attribute.Value.IsNil then None else Some attribute.Value })

            for (update, _) in methodUpdatesWithDefs do
                let methodKey = update.MethodKey
                if methodsWithCustomAttribute.Contains methodKey |> not then
                    match tryFindStateMachineType methodKey with
                    | ValueSome stateMachineHandle ->
                        let ctorToken = ensureAsyncAttributeCtor ()
                        let ctorRowId = ctorToken &&& 0x00FFFFFF
                        let methodRowId = methodDefinitionIndex.GetRowId methodKey
                        let stateMachineFullName = getFullTypeName stateMachineHandle
                        let valueBytes = encodeAsyncAttributeValue stateMachineFullName

                        nextRowId <- nextRowId + 1
                        rows.Add(
                            { RowId = nextRowId
                              Parent = struct (HandleKind.MethodDefinition, methodRowId)
                              Constructor = struct (HandleKind.MemberReference, ctorRowId)
                              Value = valueBytes
                              ValueHandle = None })

                        methodsWithCustomAttribute.Add methodKey |> ignore
                    | ValueNone -> ()

            if nullableContextAttributeSeen then
                for KeyValue(methodRowId, methodKey) in methodRowIdToKey do
                    if methodsWithNullableContextAttribute.Contains methodKey |> not then
                        let ctorToken = ensureNullableContextAttributeCtor ()
                        let ctorRowId = ctorToken &&& 0x00FFFFFF
                        nextRowId <- nextRowId + 1
                        rows.Add(
                            { RowId = nextRowId
                              Parent = struct (HandleKind.MethodDefinition, methodRowId)
                              Constructor = struct (HandleKind.MemberReference, ctorRowId)
                              Value = encodeNullableContextValue ()
                              ValueHandle = None })
                        methodsWithNullableContextAttribute.Add methodKey |> ignore

            let rowList = rows |> Seq.toList
            if traceMetadata.Value then
                printfn "[fsharp-hotreload][metadata] custom-attributes rows=%d" (List.length rowList)
            rowList

        let typeReferenceRowList =
            typeReferenceRows
            |> Seq.sortBy (fun row -> row.RowId)
            |> Seq.toList

        let memberReferenceRowList =
            memberReferenceRows
            |> Seq.sortBy (fun row -> row.RowId)
            |> Seq.toList

        let assemblyReferenceRowList =
            assemblyReferenceRows
            |> Seq.sortBy (fun row -> row.RowId)
            |> Seq.toList

        if traceMetadata.Value then
            printfn
                "[fsharp-hotreload][metadata] row-counts typeRef=%d memberRef=%d assemblyRef=%d customAttr=%d"
                typeReferenceRowList.Length
                memberReferenceRowList.Length
                assemblyReferenceRowList.Length
                customAttributeRowList.Length
            for row in typeReferenceRowList do
                let struct (scopeKind, scopeRowId) = row.ResolutionScope
                printfn
                    "[fsharp-hotreload][metadata] typeref rowId=%d name=%s scope=%A row=%d"
                    row.RowId
                    row.Name
                    scopeKind
                    scopeRowId
            for row in memberReferenceRowList do
                let struct (parentKind, parentRowId) = row.Parent
                printfn
                    "[fsharp-hotreload][metadata] memberref rowId=%d name=%s parent=%A row=%d"
                    row.RowId
                    row.Name
                    parentKind
                    parentRowId
            for row in assemblyReferenceRowList do
                printfn "[fsharp-hotreload][metadata] assemblyref rowId=%d name=%s" row.RowId row.Name

        let streams = builder.Build()

        let metadataDelta =
            MetadataWriter.emitWithReferences
                metadataBuilder
                moduleName
                baselineModuleNameHandle
                request.CurrentGeneration
                encId
                encBaseId
                moduleMvid
                methodDefinitionRowsSnapshot
                parameterDefinitionRowsSnapshot
                typeReferenceRowList
                memberReferenceRowList
                assemblyReferenceRowList
                propertyDefinitionRowsSnapshot
                eventDefinitionRowsSnapshot
                propertyMapRowsSnapshot
                eventMapRowsSnapshot
                methodSemanticsRowsSnapshot
                streams.StandaloneSignatures
                customAttributeRowList
                userStringEntries
                methodUpdates
                baselineHeapOffsets
                request.Baseline.Metadata.TableRowCounts

        if traceMetadata.Value then
            let count idx = metadataDelta.TableRowCounts.[int idx]
            printfn
                "[fsharp-hotreload][metadata] table-counts module=%d method=%d param=%d typeRef=%d memberRef=%d assemblyRef=%d customAttr=%d standAloneSig=%d"
                (count TableIndex.Module)
                (count TableIndex.MethodDef)
                (count TableIndex.Param)
                (count TableIndex.TypeRef)
                (count TableIndex.MemberRef)
                (count TableIndex.AssemblyRef)
                (count TableIndex.CustomAttribute)
                (count TableIndex.StandAloneSig)

        let addedOrChangedMethods =
            streams.MethodBodies
            |> List.map (fun body ->
                { HotReloadBaseline.AddedOrChangedMethodInfo.MethodToken = body.MethodToken
                  LocalSignatureToken = body.LocalSignatureToken
                  CodeOffset = body.CodeOffset
                  CodeLength = body.CodeLength })

        let deltaToUpdatedMethodToken =
            let dict = Dictionary<int, int>()
            for KeyValue(newToken, baselineToken) in methodTokenMap do
                dict[baselineToken] <- newToken
            for methodInfo in addedOrChangedMethods do
                if not (dict.ContainsKey methodInfo.MethodToken) then
                    dict[methodInfo.MethodToken] <- methodInfo.MethodToken
            dict :> IReadOnlyDictionary<_, _>

        let pdbDelta =
            match pdbBytesOpt with
            | None -> None
            | Some pdbBytes ->
                HotReloadPdb.emitDelta
                    request.Baseline
                    pdbBytes
                    addedOrChangedMethods
                    deltaToUpdatedMethodToken
                    metadataDelta.EncLog
                    metadataDelta.EncMap

        let synthesizedSnapshot =
            request.SynthesizedNames
            |> Option.map (fun map -> map.Snapshot |> Map.ofSeq)

        let updatedBaselineCore =
            HotReloadBaseline.applyDelta
                request.Baseline
                metadataDelta.TableRowCounts
                metadataDelta.HeapSizes
                addedOrChangedMethods
                encId
                encBaseId
                synthesizedSnapshot

        let addPropertyMapEntry (entries: Map<string, int>) (row: PropertyMapRowInfo) =
            if row.IsAdded then
                entries |> Map.add row.DeclaringType row.RowId
            else
                entries

        let addEventMapEntry (entries: Map<string, int>) (row: EventMapRowInfo) =
            if row.IsAdded then
                entries |> Map.add row.DeclaringType row.RowId
            else
                entries

        let extendMethodSemanticsMap
            (entries: Map<MethodDefinitionKey, MethodSemanticsEntry list>)
            (row: MethodSemanticsMetadataUpdate)
            =
            if row.IsAdded then
                match methodTokenToKey.TryGetValue row.MethodToken with
                | true, methodKey ->
                    match row.AssociationInfo with
                    | Some association ->
                        let newEntry =
                            { MethodSemanticsEntry.RowId = row.RowId
                              Attributes = row.Attributes
                              Association = association }

                        let updatedList =
                            match entries |> Map.tryFind methodKey with
                            | Some existing ->
                                newEntry :: existing
                                |> List.distinctBy (fun entry -> entry.RowId)
                            | None -> [ newEntry ]

                        entries |> Map.add methodKey updatedList
                    | None -> entries
                | _ -> entries
            else
                entries

        let updatedPropertyMapEntries =
            propertyMapRowsSnapshot
            |> List.fold addPropertyMapEntry updatedBaselineCore.PropertyMapEntries

        let updatedEventMapEntries =
            eventMapRowsSnapshot
            |> List.fold addEventMapEntry updatedBaselineCore.EventMapEntries

        let updatedMethodSemanticsEntries =
            methodSemanticsRowsSnapshot
            |> List.fold extendMethodSemanticsMap updatedBaselineCore.MethodSemanticsEntries

        let updatedMethodTokenMap =
            addedMethodDeltaTokens
            |> Seq.fold (fun acc (KeyValue(key, token)) -> acc |> Map.add key token) updatedBaselineCore.MethodTokens

        let updatedPropertyTokenMap =
            addedPropertyDeltaTokens
            |> Seq.fold (fun acc (KeyValue(key, token)) -> acc |> Map.add key token) updatedBaselineCore.PropertyTokens

        let updatedEventTokenMap =
            addedEventDeltaTokens
            |> Seq.fold (fun acc (KeyValue(key, token)) -> acc |> Map.add key token) updatedBaselineCore.EventTokens

        let updatedBaseline =
            { updatedBaselineCore with
                MethodTokens = updatedMethodTokenMap
                PropertyTokens = updatedPropertyTokenMap
                EventTokens = updatedEventTokenMap
                PropertyMapEntries = updatedPropertyMapEntries
                EventMapEntries = updatedEventMapEntries
                MethodSemanticsEntries = updatedMethodSemanticsEntries }

        { emptyDelta with
            Metadata = metadataDelta.Metadata
            IL = streams.IL
            UpdatedTypeTokens = updatedTypeTokens
            UpdatedMethodTokens = updatedMethodTokenList
            EncLog = metadataDelta.EncLog
            EncMap = metadataDelta.EncMap
            MethodBodies = streams.MethodBodies
            StandaloneSignatures = streams.StandaloneSignatures
            Pdb = pdbDelta
            GenerationId = encId
            BaseGenerationId = encBaseId
            UserStringUpdates = userStringUpdates |> Seq.toList
            MethodDefinitionRows = methodDefinitionRowsSnapshot
            AddedOrChangedMethods = addedOrChangedMethods
            UpdatedBaseline = Some updatedBaseline
        }
        |> fun delta ->
            if traceUserStringUpdates.Value then
                for (original, updated, text) in delta.UserStringUpdates do
                    printfn "[fsharp-hotreload][userstring-summary] original=0x%08X new=0x%08X text=%s" original updated text
            delta
