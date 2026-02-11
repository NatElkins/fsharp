module internal FSharp.Compiler.HotReload.DeltaBuilder

open System
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReload.DefinitionMap
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeDiff

let private isEnvVarTruthy (name: string) =
    match Environment.GetEnvironmentVariable(name) with
    | null -> false
    | value when String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) -> true
    | value when String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) -> true
    | _ -> false

let private traceMethodResolution = isEnvVarTruthy "FSHARP_HOTRELOAD_TRACE_METHODS"

let private checkedFiles (CheckedAssemblyAfterOptimization impls) =
    impls
    |> List.map (fun afterOpt -> afterOpt.ImplFile)

let private fileKey (CheckedImplFile(qualifiedNameOfFile = qual)) = qual.Text

let private buildLookup (files: CheckedImplFile list) =
    files |> List.map (fun file -> fileKey file, file) |> Map.ofList

let private emptyDefinitionMap: FSharpDefinitionMap =
    { Changes = []
      RudeEdits = [] }

let private mergeDefinitionMaps (left: FSharpDefinitionMap) (right: FSharpDefinitionMap) : FSharpDefinitionMap =
    { Changes = left.Changes @ right.Changes
      RudeEdits = left.RudeEdits @ right.RudeEdits }

let computeSymbolChanges
    (tcGlobals: TcGlobals)
    (baseline: CheckedAssemblyAfterOptimization)
    (updated: CheckedAssemblyAfterOptimization)
    : FSharpSymbolChanges =
    let baselineFiles = checkedFiles baseline
    let updatedFiles = checkedFiles updated

    let baselineLookup = buildLookup baselineFiles

    let definitionMap =
        (emptyDefinitionMap, updatedFiles)
        ||> List.fold (fun acc updatedFile ->
            match Map.tryFind (fileKey updatedFile) baselineLookup with
            | Some baselineFile ->
                let diff = diffImplementationFile tcGlobals baselineFile updatedFile
                let map = FSharpDefinitionMap.ofTypedTreeDiff diff
                mergeDefinitionMaps acc map
            | None ->
                // For now treat unmatched files as unsupported edits by generating a rude edit placeholder.
                let rudeEdit =
                    { Symbol = None
                      Kind = RudeEditKind.Unsupported
                      Message = $"File '{fileKey updatedFile}' is new or renamed; full rebuild required." }

                mergeDefinitionMaps acc { emptyDefinitionMap with RudeEdits = [ rudeEdit ] })

    FSharpSymbolChanges.ofDefinitionMap definitionMap

let private joinPath (segments: string list) = String.concat "." segments

let private deduplicate list = list |> List.fold (fun acc item -> if List.contains item acc then acc else item :: acc) [] |> List.rev

let private deduplicateSymbols symbols =
    symbols
    |> List.fold (fun acc symbol ->
        if acc |> List.exists (fun existing -> existing.Stamp = symbol.Stamp) then acc else symbol :: acc)
        []
    |> List.rev

let private methodNameOfSymbol (symbol: SymbolId) =
    symbol.CompiledName |> Option.defaultValue symbol.LogicalName

let rec private ilTypeIdentity (ilType: ILType) =
    match ilType with
    | ILType.Void -> "System.Void"
    | ILType.Array(ILArrayShape shape, elementType) ->
        let rankSuffix =
            if shape.Length <= 1 then
                "[]"
            else
                "[" + String(',', shape.Length - 1) + "]"

        ilTypeIdentity elementType + rankSuffix
    | ILType.Value typeSpec
    | ILType.Boxed typeSpec -> ilTypeSpecIdentity typeSpec
    | ILType.Ptr elementType -> ilTypeIdentity elementType + "*"
    | ILType.Byref elementType -> ilTypeIdentity elementType + "&"
    | ILType.FunctionPointer signature ->
        let args = signature.ArgTypes |> List.map ilTypeIdentity |> String.concat ","
        $"{ilTypeIdentity signature.ReturnType} ({args})"
    | ILType.TypeVar index -> "!" + string index
    | ILType.Modified(_, _, innerType) -> ilTypeIdentity innerType

and private ilTypeSpecIdentity (typeSpec: ILTypeSpec) =
    let fullName = typeSpec.TypeRef.FullName

    if List.isEmpty typeSpec.GenericArgs then
        fullName
    else
        let encodedArgs =
            typeSpec.GenericArgs
            |> List.map (fun arg -> $"[{ilTypeIdentity arg}]")
            |> String.concat ","

        $"{fullName}[{encodedArgs}]"

let private methodKeyMatchesSymbol (symbol: SymbolId) (key: MethodDefinitionKey) =
    let nameMatches = String.Equals(key.Name, methodNameOfSymbol symbol, StringComparison.Ordinal)

    let argCountMatches =
        match symbol.TotalArgCount with
        | Some count -> key.ParameterTypes.Length = count
        | None -> true

    let genericArityMatches =
        match symbol.GenericArity with
        | Some arity -> key.GenericArity = arity
        | None -> true

    nameMatches && argCountMatches && genericArityMatches

let private methodParameterTypesMatchSymbol (symbol: SymbolId) (key: MethodDefinitionKey) =
    match symbol.ParameterTypeIdentities with
    | Some parameterTypeIdentities ->
        let methodParameterTypes = key.ParameterTypes |> List.map ilTypeIdentity
        methodParameterTypes = parameterTypeIdentities
    | None -> false

let private methodReturnTypeMatchesSymbol (symbol: SymbolId) (key: MethodDefinitionKey) =
    match symbol.ReturnTypeIdentity with
    | Some returnTypeIdentity -> ilTypeIdentity key.ReturnType = returnTypeIdentity
    | None -> false

let mapSymbolChangesToDelta
    (baseline: FSharpEmitBaseline)
    (changes: FSharpSymbolChanges)
    : string list * MethodDefinitionKey list * AccessorUpdate list =

    if traceMethodResolution then
        let formatSymbol (symbol: SymbolId) =
            sprintf
                "name=%s path=%A kind=%A memberKind=%A synthesized=%b"
                symbol.LogicalName
                symbol.Path
                symbol.Kind
                symbol.MemberKind
                symbol.IsSynthesized

        let formatUpdated (change: UpdatedSymbolChange) =
            sprintf
                "%s semanticEdit=%A containingEntity=%A"
                (formatSymbol change.Symbol)
                change.Kind
                change.ContainingEntity

        let addedText =
            changes.Added
            |> List.map formatSymbol
            |> String.concat " | "

        let deletedText =
            changes.Deleted
            |> List.map formatSymbol
            |> String.concat " | "

        let updatedText =
            changes.Updated
            |> List.map formatUpdated
            |> String.concat " | "

        printfn
            "[fsharp-hotreload][delta-builder] changes summary: added=[%s] deleted=[%s] updated=[%s]"
            addedText
            deletedText
            updatedText

    let candidateEntityNames (symbol: SymbolId) =
        let segments = symbol.Path @ [ symbol.LogicalName ]

        let rec tails acc remaining =
            match remaining with
            | [] -> List.rev acc
            | _ :: tail as segs ->
                let name = joinPath segs
                tails (name :: acc) tail

        tails [] segments

    let tryResolveTypeName (names: string list) =
        names |> List.tryFind (fun name -> Map.containsKey name baseline.TypeTokens)

    let updatedTypes =
        changes
        |> FSharpSymbolChanges.entitySymbolsWithChanges
        |> List.choose (fun symbol ->
            symbol
            |> candidateEntityNames
            |> tryResolveTypeName)
        |> deduplicate

    let candidateContainingTypeNames (change: UpdatedSymbolChange) =
        let pathSuffixes =
            let rec tails acc segments =
                match segments with
                | [] -> List.rev acc
                | _ :: tail as segs -> tails (joinPath segs :: acc) tail

            tails [] change.Symbol.Path

        let explicitEntity =
            match change.ContainingEntity with
            | Some name -> [ name ]
            | None -> []

        deduplicate (explicitEntity @ pathSuffixes)

    let tryResolveMethodKey symbol typeName =
        let candidates =
            baseline.MethodTokens
            |> Map.toSeq
            |> Seq.choose (fun (key, _) ->
                if key.DeclaringType = typeName && methodKeyMatchesSymbol symbol key then
                    Some key
                else
                    None)
            |> Seq.toList

        match candidates with
        | [] -> None
        | [ candidate ] -> Some candidate
        | _ ->
            let parameterMatchedCandidates =
                if symbol.ParameterTypeIdentities.IsSome then
                    candidates |> List.filter (methodParameterTypesMatchSymbol symbol)
                else
                    candidates

            match parameterMatchedCandidates with
            | [ candidate ] -> Some candidate
            | [] -> None
            | _ ->
                // Return type disambiguation mirrors Roslyn's signature equality only after parameter matching.
                let returnMatchedCandidates =
                    if symbol.ReturnTypeIdentity.IsSome then
                        parameterMatchedCandidates |> List.filter (methodReturnTypeMatchesSymbol symbol)
                    else
                        parameterMatchedCandidates

                match returnMatchedCandidates with
                | [ candidate ] -> Some candidate
                | _ -> None

    let updatedMethods =
        changes.Updated
        |> List.choose (fun change ->
            match change.Kind with
            | SemanticEditKind.MethodBody when change.Symbol.Kind = SymbolKind.Value ->
                let candidates = candidateContainingTypeNames change
                let resolved = candidates |> List.tryPick (fun typeName -> tryResolveMethodKey change.Symbol typeName)

                if traceMethodResolution then
                    printfn
                        "[fsharp-hotreload][delta-builder] symbol=%s compiledName=%A args=%A genericArity=%A parameterTypes=%A returnType=%A path=%A containingEntity=%A candidates=%A resolved=%A"
                        change.Symbol.LogicalName
                        change.Symbol.CompiledName
                        change.Symbol.TotalArgCount
                        change.Symbol.GenericArity
                        change.Symbol.ParameterTypeIdentities
                        change.Symbol.ReturnTypeIdentity
                        change.Symbol.Path
                        change.ContainingEntity
                        candidates
                        (resolved |> Option.map (fun key -> sprintf "%s::%s" key.DeclaringType key.Name))

                resolved
            | _ -> None)
        |> deduplicate

    let accessorSymbols =
        [ yield! FSharpSymbolChanges.propertyAccessorsAdded changes
          yield! FSharpSymbolChanges.propertyAccessorsUpdated changes |> List.map (fun change -> change.Symbol)
          yield! FSharpSymbolChanges.propertyAccessorsDeleted changes
          yield! FSharpSymbolChanges.eventAccessorsAdded changes
          yield! FSharpSymbolChanges.eventAccessorsUpdated changes |> List.map (fun change -> change.Symbol)
          yield! FSharpSymbolChanges.eventAccessorsDeleted changes ]
        |> List.filter (fun symbol ->
            match symbol.MemberKind with
            | Some SymbolMemberKind.Method -> false
            | Some _ -> true
            | None -> false)
        |> deduplicateSymbols

    let accessorUpdates =
        accessorSymbols
        |> List.choose (fun symbol ->
            symbol
            |> candidateEntityNames
            |> tryResolveTypeName
            |> Option.map (fun typeName ->
                let methodKey = tryResolveMethodKey symbol typeName
                { AccessorUpdate.Symbol = symbol
                  ContainingType = typeName
                  MemberKind = symbol.MemberKind.Value
                  Method = methodKey }))

    updatedTypes, updatedMethods, accessorUpdates
