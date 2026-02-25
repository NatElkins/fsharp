module internal FSharp.Compiler.HotReload.DeltaBuilder

open System
open FSharp.Compiler.EnvironmentHelpers
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.HotReload
open FSharp.Compiler.HotReload.DefinitionMap
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeDiff

let private traceMethodResolution = isEnvVarTruthy "FSHARP_HOTRELOAD_TRACE_METHODS"

let private checkedFiles (CheckedAssemblyAfterOptimization impls) =
    impls
    |> Seq.map (fun afterOpt -> afterOpt.ImplFile)

let private fileKey (CheckedImplFile(qualifiedNameOfFile = qual)) = qual.Text

let private buildLookup (files: seq<CheckedImplFile>) =
    files |> Seq.map (fun file -> fileKey file, file) |> Map.ofSeq

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
        ||> Seq.fold (fun acc updatedFile ->
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

// Normalize nested type separators ("+" vs ".") so symbol/baseline matching is resilient
// to representation differences while still using canonical baseline names in emitted deltas.
let private splitTypePath (typeName: string) =
    typeName.Split([| '.'; '+' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

let private buildTypePathLookup (typeTokens: Map<string, int>) =
    typeTokens
    |> Map.toSeq
    |> Seq.fold
        (fun acc (name, _) ->
            let key = splitTypePath name
            let existing = acc |> Map.tryFind key |> Option.defaultValue []
            acc |> Map.add key (name :: existing))
        Map.empty

let private tryResolveTypeNameByPath
    (typeTokens: Map<string, int>)
    (typePathLookup: Map<string list, string list>)
    (names: string list)
    =
    names
    |> List.tryPick (fun name ->
        match typePathLookup |> Map.tryFind (splitTypePath name) with
        | Some [ resolved ] -> Some resolved
        | Some resolvedNames ->
            resolvedNames
            |> List.tryFind (fun candidate -> typeTokens |> Map.containsKey candidate)
        | None -> None)

let private typeNamesEquivalent (left: string) (right: string) =
    String.Equals(left, right, StringComparison.Ordinal)
    || splitTypePath left = splitTypePath right

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

let private formatSymbolIdentity (symbol: SymbolId) =
    let path =
        match symbol.Path with
        | [] -> "<global>"
        | _ -> joinPath symbol.Path

    let memberName = methodNameOfSymbol symbol
    $"{path}::{memberName}"

type private MethodResolutionResult =
    | MethodResolved of MethodDefinitionKey
    | MethodIdentityMissing of string list
    | MethodMissing
    | MethodAmbiguous of MethodDefinitionKey list

type private MethodIdentityKey =
    { DeclaringTypeToken: int
      Name: string
      GenericArity: int
      ParameterTypes: string list
      ReturnType: string }

let private methodIdentityKey (declaringTypeToken: int) (methodKey: MethodDefinitionKey) : MethodIdentityKey =
    { DeclaringTypeToken = declaringTypeToken
      Name = methodKey.Name
      GenericArity = methodKey.GenericArity
      ParameterTypes = methodKey.ParameterTypes |> List.map ilTypeIdentity
      ReturnType = ilTypeIdentity methodKey.ReturnType }

let private tryMethodIdentityKeyFromSymbol (declaringTypeToken: int) (symbol: SymbolId) : MethodIdentityKey option =
    match symbol.GenericArity, symbol.ParameterTypeIdentities, symbol.ReturnTypeIdentity with
    | Some genericArity, Some parameterTypes, Some returnType ->
        Some
            { DeclaringTypeToken = declaringTypeToken
              Name = methodNameOfSymbol symbol
              GenericArity = genericArity
              ParameterTypes = parameterTypes
              ReturnType = returnType }
    | _ -> None

let private describeMethodKey (key: MethodDefinitionKey) =
    let parameterCount = key.ParameterTypes.Length
    $"{key.DeclaringType}::{key.Name}/{parameterCount}`{key.GenericArity}"

let private missingRuntimeSignatureIdentityParts (symbol: SymbolId) =
    [ if symbol.CompiledName.IsNone then
          yield "compiled name"
      if symbol.TotalArgCount.IsNone then
          yield "argument count"
      if symbol.GenericArity.IsNone then
          yield "generic arity"
      if symbol.ParameterTypeIdentities.IsNone then
          yield "parameter type identities"
      if symbol.ReturnTypeIdentity.IsNone then
          yield "return type identity" ]

// Maps typed-tree symbol changes to baseline tokens using fail-closed matching:
// unresolved or ambiguous bindings return errors instead of silently dropping edits.
let mapSymbolChangesToDelta
    (baseline: FSharpEmitBaseline)
    (changes: FSharpSymbolChanges)
    : Result<string list * MethodDefinitionKey list * AccessorUpdate list, string list> =

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

    let typePathLookup = buildTypePathLookup baseline.TypeTokens

    let tryResolveTypeName (names: string list) =
        names
        |> List.tryFind (fun name -> Map.containsKey name baseline.TypeTokens)
        |> Option.orElseWith (fun () -> tryResolveTypeNameByPath baseline.TypeTokens typePathLookup names)

    let methodIdentityIndex =
        let index = System.Collections.Generic.Dictionary<MethodIdentityKey, ResizeArray<MethodDefinitionKey>>(HashIdentity.Structural)

        for KeyValue(methodKey, _) in baseline.MethodTokens do
            match baseline.TypeTokens |> Map.tryFind methodKey.DeclaringType with
            | Some declaringTypeToken ->
                let identity = methodIdentityKey declaringTypeToken methodKey

                let bucket =
                    match index.TryGetValue identity with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<MethodDefinitionKey>()
                        index[identity] <- created
                        created

                bucket.Add methodKey
            | None -> ()

        index

    let lookupMethodsByIdentity (symbol: SymbolId) (typeNames: string list) =
        let typeTokens =
            baseline.TypeTokens
            |> Map.toSeq
            |> Seq.choose (fun (declaringTypeName, declaringTypeToken) ->
                if typeNames |> List.exists (typeNamesEquivalent declaringTypeName) then
                    Some declaringTypeToken
                else
                    None)
            |> Seq.toList
            |> deduplicate

        let matchedMethods =
            typeTokens
            |> List.collect (fun typeToken ->
                match tryMethodIdentityKeyFromSymbol typeToken symbol with
                | Some identity ->
                    match methodIdentityIndex.TryGetValue identity with
                    | true, methods -> methods |> Seq.toList
                    | _ -> []
                | None -> [])
            |> deduplicate

        typeTokens, matchedMethods

    let updatedTypes, typeResolutionErrors =
        changes
        |> FSharpSymbolChanges.entitySymbolsWithChanges
        |> List.fold (fun (resolvedTypes, errors) symbol ->
            let candidates = symbol |> candidateEntityNames

            match candidates |> tryResolveTypeName with
            | Some resolvedTypeName -> resolvedTypeName :: resolvedTypes, errors
            | None ->
                let errorMessage =
                    $"Unable to resolve changed type symbol '{formatSymbolIdentity symbol}' to a baseline type token (candidates={candidates}); full rebuild required."

                resolvedTypes, errorMessage :: errors)
            ([], [])

    let updatedTypes = updatedTypes |> List.rev |> deduplicate
    let typeResolutionErrors = typeResolutionErrors |> List.rev

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

    let resolveContainingTypeCandidates (change: UpdatedSymbolChange) =
        let rawCandidates = candidateContainingTypeNames change

        let normalizedCandidates =
            rawCandidates
            |> List.choose (fun candidate -> tryResolveTypeName [ candidate ])
            |> deduplicate

        match change.ContainingEntity with
        | Some explicitEntity ->
            match tryResolveTypeName [ explicitEntity ] with
            | Some resolvedExplicit -> Ok [ resolvedExplicit ]
            | None ->
                Error
                    ($"Unable to resolve explicit containing entity '{explicitEntity}' for symbol '{formatSymbolIdentity change.Symbol}' to a baseline type token; full rebuild required.")
        | None ->
            if List.isEmpty normalizedCandidates then
                Ok rawCandidates
            else
                Ok normalizedCandidates

    let resolveMethodKey (symbol: SymbolId) (typeNames: string list) =
        let missingIdentityParts = missingRuntimeSignatureIdentityParts symbol

        if not (List.isEmpty missingIdentityParts) then
            // Fail closed: if we cannot describe the runtime method signature precisely,
            // avoid best-effort token matching that could map edits to the wrong method.
            MethodIdentityMissing missingIdentityParts
        else
            let _, identityMatchedCandidates = lookupMethodsByIdentity symbol typeNames

            match identityMatchedCandidates with
            | [ candidate ] -> MethodResolved candidate
            | _ :: _ as ambiguous -> MethodAmbiguous ambiguous
            | [] ->
                let candidates =
                    baseline.MethodTokens
                    |> Map.toSeq
                    |> Seq.choose (fun (key, _) ->
                        if (typeNames |> List.exists (typeNamesEquivalent key.DeclaringType)) && methodKeyMatchesSymbol symbol key then
                            Some key
                        else
                            None)
                    |> Seq.distinct
                    |> Seq.toList

                match candidates with
                | [] -> MethodMissing
                | [ candidate ] -> MethodResolved candidate
                | _ ->
                    let parameterMatchedCandidates =
                        candidates |> List.filter (methodParameterTypesMatchSymbol symbol)

                    match parameterMatchedCandidates with
                    | [ candidate ] -> MethodResolved candidate
                    | [] -> MethodMissing
                    | _ ->
                        // Return type disambiguation mirrors Roslyn's signature equality only after parameter matching.
                        let returnMatchedCandidates =
                            parameterMatchedCandidates |> List.filter (methodReturnTypeMatchesSymbol symbol)

                        match returnMatchedCandidates with
                        | [ candidate ] -> MethodResolved candidate
                        | [] -> MethodMissing
                        | ambiguous -> MethodAmbiguous ambiguous

    let updatedMethods, methodResolutionErrors =
        changes.Updated
        |> List.fold (fun (resolvedMethods, errors) change ->
            match change.Kind with
            | SemanticEditKind.MethodBody when change.Symbol.Kind = SymbolKind.Value ->
                match resolveContainingTypeCandidates change with
                | Error errorMessage ->
                    resolvedMethods, errorMessage :: errors
                | Ok candidates ->
                    let resolution = resolveMethodKey change.Symbol candidates

                    if traceMethodResolution then
                        printfn
                            "[fsharp-hotreload][delta-builder] symbol=%s compiledName=%A args=%A genericArity=%A parameterTypes=%A returnType=%A path=%A containingEntity=%A candidates=%A resolution=%A"
                            change.Symbol.LogicalName
                            change.Symbol.CompiledName
                            change.Symbol.TotalArgCount
                            change.Symbol.GenericArity
                            change.Symbol.ParameterTypeIdentities
                            change.Symbol.ReturnTypeIdentity
                            change.Symbol.Path
                            change.ContainingEntity
                            candidates
                            resolution

                    match resolution with
                    | MethodResolved methodKey -> methodKey :: resolvedMethods, errors
                    | MethodIdentityMissing missingParts ->
                        let missingText = String.concat ", " missingParts
                        let errorMessage =
                            $"Unable to resolve changed method symbol '{formatSymbolIdentity change.Symbol}' because runtime signature identity is incomplete (missing: {missingText}); full rebuild required."

                        resolvedMethods, errorMessage :: errors
                    | MethodMissing ->
                        let errorMessage =
                            $"Unable to resolve changed method symbol '{formatSymbolIdentity change.Symbol}' to a unique baseline method token (containingTypeCandidates={candidates}); full rebuild required."

                        resolvedMethods, errorMessage :: errors
                    | MethodAmbiguous ambiguous ->
                        let ambiguousText = ambiguous |> List.map describeMethodKey |> String.concat "; "
                        let errorMessage =
                            $"Ambiguous baseline method mapping for '{formatSymbolIdentity change.Symbol}' (containingTypeCandidates={candidates}, matches=[{ambiguousText}]); full rebuild required."

                        resolvedMethods, errorMessage :: errors
            | _ -> resolvedMethods, errors)
            ([], [])

    let updatedMethods = updatedMethods |> List.rev |> deduplicate
    let methodResolutionErrors = methodResolutionErrors |> List.rev

    let accessorSymbols =
        [ yield! FSharpSymbolChanges.propertyAccessorsAdded changes
          yield! FSharpSymbolChanges.propertyAccessorsUpdated changes |> Seq.map (fun change -> change.Symbol)
          yield! FSharpSymbolChanges.propertyAccessorsDeleted changes
          yield! FSharpSymbolChanges.eventAccessorsAdded changes
          yield! FSharpSymbolChanges.eventAccessorsUpdated changes |> Seq.map (fun change -> change.Symbol)
          yield! FSharpSymbolChanges.eventAccessorsDeleted changes ]
        |> List.filter (fun symbol ->
            match symbol.MemberKind with
            | Some SymbolMemberKind.Method -> false
            | Some _ -> true
            | None -> false)
        |> deduplicateSymbols

    let accessorUpdates, accessorResolutionErrors =
        accessorSymbols
        |> List.fold (fun (resolvedAccessors, errors) symbol ->
            let containingTypeCandidates = symbol |> candidateEntityNames

            match containingTypeCandidates |> tryResolveTypeName with
            | None -> resolvedAccessors, errors
            | Some typeName ->
                let method, updatedErrors =
                    match resolveMethodKey symbol [ typeName ] with
                    | MethodResolved methodKey -> Some methodKey, errors
                    | MethodIdentityMissing missingParts ->
                        let missingText = String.concat ", " missingParts
                        let errorMessage =
                            $"Unable to resolve accessor symbol '{formatSymbolIdentity symbol}' because runtime signature identity is incomplete (missing: {missingText}); full rebuild required."

                        None, errorMessage :: errors
                    | MethodMissing ->
                        let errorMessage =
                            $"Unable to resolve accessor symbol '{formatSymbolIdentity symbol}' to a unique baseline method token (type={typeName}); full rebuild required."

                        None, errorMessage :: errors
                    | MethodAmbiguous ambiguous ->
                        let ambiguousText = ambiguous |> List.map describeMethodKey |> String.concat "; "
                        let errorMessage =
                            $"Ambiguous accessor method mapping for '{formatSymbolIdentity symbol}' (type={typeName}, matches=[{ambiguousText}]); full rebuild required."

                        None, errorMessage :: errors

                { AccessorUpdate.Symbol = symbol
                  ContainingType = typeName
                  MemberKind = symbol.MemberKind.Value
                  Method = method }
                :: resolvedAccessors, updatedErrors)
            ([], [])

    let accessorUpdates = accessorUpdates |> List.rev
    let accessorResolutionErrors = accessorResolutionErrors |> List.rev

    let resolutionErrors =
        typeResolutionErrors @ methodResolutionErrors @ accessorResolutionErrors
        |> deduplicate

    if List.isEmpty resolutionErrors then
        Ok(updatedTypes, updatedMethods, accessorUpdates)
    else
        Error resolutionErrors
