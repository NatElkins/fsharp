module internal FSharp.Compiler.IlxDeltaEmitter

open System
open System.Collections.Generic
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILDelta
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline

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

    let encLog, encMap = buildEncTables updatedTypeTokens updatedMethodTokens

    { emptyDelta with
        UpdatedTypeTokens = updatedTypeTokens
        UpdatedMethodTokens = updatedMethodTokens
        EncLog = encLog
        EncMap = encMap
    }
