// Copyright (c) Microsoft Corporation. All Rights Reserved.

module internal FSharp.Compiler.TypedTreeDiff

open System
open System.Collections.Generic
open System.Text

open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeOps

/// Describes the high-level category for a symbol participating in a hot reload edit.
[<RequireQualifiedAccess>]
type SymbolKind =
    | Value
    | Entity

/// Stable identity for values and entities tracked across baseline/hot reload sessions.
type SymbolId =
    { Path: string list
      LogicalName: string
      Stamp: Stamp
      Kind: SymbolKind }

    member x.QualifiedName =
        match x.Path with
        | [] -> x.LogicalName
        | path -> String.concat "." (path @ [ x.LogicalName ])

[<RequireQualifiedAccess>]
type SemanticEditKind =
    | MethodBody
    | Insert
    | Delete
    | TypeDefinition

[<RequireQualifiedAccess>]
type RudeEditKind =
    | SignatureChange
    | InlineChange
    | TypeLayoutChange
    | DeclarationAdded
    | DeclarationRemoved
    | Unsupported

type SemanticEdit =
    { Symbol: SymbolId
      Kind: SemanticEditKind
      BaselineHash: int option
      UpdatedHash: int option }

type RudeEdit =
    { Symbol: SymbolId option
      Kind: RudeEditKind
      Message: string }

type TypedTreeDiffResult =
    { SemanticEdits: SemanticEdit list
      RudeEdits: RudeEdit list }

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private stableHash (text: string) =
    let mutable hash = 23

    if not (String.IsNullOrEmpty text) then
        for ch in text do
            hash <- (hash * 31) + int ch

    hash

let private hashCombine (seed: int) (value: int) = (seed * 16777619) ^^^ value

let private hashList (items: seq<int>) =
    let mutable acc = 1

    for item in items do
        acc <- hashCombine acc item

    acc

let private tyToString (_: DisplayEnv) (ty: TType) =
    sprintf "%A" ty

let private constDigest (c: Const) =
    match c with
    | Const.Bool v -> if v then "true" else "false"
    | Const.SByte v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Int16 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Int32 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Int64 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Byte v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.UInt16 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.UInt32 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.UInt64 v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.IntPtr v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.UIntPtr v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Single v -> v.ToString("r", Globalization.CultureInfo.InvariantCulture)
    | Const.Double v -> v.ToString("r", Globalization.CultureInfo.InvariantCulture)
    | Const.String v -> "\"" + v + "\""
    | Const.Char v -> "'" + string v + "'"
    | Const.Decimal v -> v.ToString("g", Globalization.CultureInfo.InvariantCulture)
    | Const.Unit -> "()"
    | Const.Zero -> "zero"

let rec private exprDigest (denv: DisplayEnv) (expr: Expr) =
    let recurse = exprDigest denv

    match expr with
    | Expr.Const (c, _, ty) ->
        [ 1
          stableHash (constDigest c)
          stableHash (tyToString denv ty) ]
        |> hashList
    | Expr.Val (vref, _, _) ->
        hashCombine 2 (int vref.Stamp)
    | Expr.App (funcExpr, _, _, args, _) ->
        let funcHash = recurse funcExpr
        let argHash = args |> List.map recurse |> hashList
        hashCombine (hashCombine 3 funcHash) argHash
    | Expr.Sequential (expr1, expr2, _, _) ->
        hashCombine (hashCombine 4 (recurse expr1)) (recurse expr2)
    | Expr.Lambda (_, _, _, valParams, bodyExpr, _, _) ->
        let paramsHash =
            valParams
            |> List.map (fun v -> stableHash v.LogicalName)
            |> hashList

        hashCombine (hashCombine 5 paramsHash) (recurse bodyExpr)
    | Expr.TyLambda (_, typars, bodyExpr, _, _) ->
        let typarHash =
            typars
            |> List.map (fun tp -> stableHash tp.DisplayName)
            |> hashList

        hashCombine (hashCombine 6 typarHash) (recurse bodyExpr)
    | Expr.Let (binding, bodyExpr, _, _) ->
        let bindHash = bindingDigest denv binding
        hashCombine (hashCombine 7 bindHash) (recurse bodyExpr)
    | Expr.LetRec (bindings, bodyExpr, _, _) ->
        let bindsHash =
            bindings
            |> List.map (bindingDigest denv)
            |> hashList

        hashCombine (hashCombine 8 bindsHash) (recurse bodyExpr)
    | Expr.Match (_, _, _, targets, _, _) ->
        let targetsHash =
            targets
            |> Array.map (fun tgt ->
                match tgt with
                | TTarget(boundVals, targetExpr, _) ->
                    let valsHash =
                        boundVals
                        |> List.map (fun v -> stableHash v.LogicalName)
                        |> hashList

                    hashCombine valsHash (recurse targetExpr))
            |> hashList

        hashCombine 9 targetsHash
    | Expr.Op (op, typeArgs, args, _) ->
        let opHash = stableHash (op.ToString())
        let argsHash = args |> List.map recurse |> hashList
        let tyHash =
            typeArgs
            |> List.map (tyToString denv >> stableHash)
            |> hashList

        [ 10; opHash; argsHash; tyHash ] |> hashList
    | Expr.Obj (_, objTy, _, ctorCall, overrides, interfaceImpls, _) ->
        let overridesHash =
            overrides
            |> List.map (fun (TObjExprMethod(_, _, _, _, body, _)) -> recurse body)
            |> hashList

        let interfaceHash =
            interfaceImpls
            |> List.map (fun (_, methods) ->
                methods
                |> List.map (fun (TObjExprMethod(_, _, _, _, body, _)) -> recurse body)
                |> hashList)
            |> hashList

        [ 11
          stableHash (tyToString denv objTy)
          recurse ctorCall
          overridesHash
          interfaceHash ]
        |> hashList
    | Expr.Quote (quotedExpr, _, _, _, _) ->
        hashCombine 12 (recurse quotedExpr)
    | Expr.DebugPoint (_, body) ->
        recurse body
    | Expr.Link eref ->
        recurse eref.Value
    | Expr.TyChoose (typars, bodyExpr, _) ->
        let typarHash =
            typars
            |> List.map (fun tp -> stableHash tp.DisplayName)
            |> hashList

        hashCombine (hashCombine 13 typarHash) (recurse bodyExpr)
    | Expr.WitnessArg (traitInfo, _) ->
        hashCombine 14 (stableHash traitInfo.MemberLogicalName)
    | Expr.StaticOptimization (_, onExpr, elseExpr, _) ->
        hashCombine (hashCombine 15 (recurse onExpr)) (recurse elseExpr)
    | _ ->
        stableHash (expr.ToString())

and private bindingDigest denv (TBind (var, body, _)) =
    let sigHash = tyToString denv var.Type |> stableHash
    hashCombine sigHash (exprDigest denv body)

type private BindingSnapshot =
    { Symbol: SymbolId
      InlineInfo: ValInline
      SignatureText: string
      BodyHash: int }

type private EntitySnapshot =
    { Symbol: SymbolId
      RepresentationHash: int
      RepresentationText: string }

let private symbolId path logicalName stamp kind =
    { Path = path
      LogicalName = logicalName
      Stamp = stamp
      Kind = kind }

let private bindingKey (snapshot: BindingSnapshot) = snapshot.Symbol.QualifiedName + "|" + snapshot.SignatureText

let private entityKey (snapshot: EntitySnapshot) = snapshot.Symbol.QualifiedName

let rec private snapshotModuleBinding denv (path: string list) (map, entities) binding =
    match binding with
    | ModuleOrNamespaceBinding.Binding b ->
        let snapshot = snapshotBinding denv path b
        (Map.add (bindingKey snapshot) snapshot map, entities)
    | ModuleOrNamespaceBinding.Module (moduleEntity, contents) ->
        snapshotModuleContents denv (path @ [ moduleEntity.LogicalName ]) (map, entities) contents

and private snapshotModuleContents denv path (map, entities) contents =
    match contents with
    | ModuleOrNamespaceContents.TMDefs defs ->
        ((map, entities), defs)
        ||> List.fold (snapshotModuleContents denv path)
    | ModuleOrNamespaceContents.TMDefLet (binding, _) ->
        let snapshot = snapshotBinding denv path binding
        (Map.add (bindingKey snapshot) snapshot map, entities)
    | ModuleOrNamespaceContents.TMDefRec (_, _, tycons, bindings, _) ->
        let entitiesWithTypes =
            (entities, tycons)
            ||> List.fold (fun acc tycon ->
                let snapshot = snapshotTycon denv path tycon
                Map.add (entityKey snapshot) snapshot acc)

        List.fold (snapshotModuleBinding denv path) (map, entitiesWithTypes) bindings
    | ModuleOrNamespaceContents.TMDefDo _ -> (map, entities)
    | ModuleOrNamespaceContents.TMDefOpens _ -> (map, entities)

and private snapshotBinding denv path (TBind (var, expr, _)) =
    let signature = tyToString denv var.Type
    let bodyHash = exprDigest denv expr
    let symbol = symbolId path var.LogicalName var.Stamp SymbolKind.Value

    { Symbol = symbol
      InlineInfo = var.InlineInfo
      SignatureText = signature
      BodyHash = bodyHash }: BindingSnapshot

and private snapshotTycon denv path (tycon: Tycon) =
    let reprText =
        let sb = StringBuilder()
        sb.Append("kind:").Append(tycon.TypeOrMeasureKind.ToString()) |> ignore

        match tycon.TypeReprInfo with
        | TFSharpTyconRepr data ->
            sb.Append("|fs-kind:").Append(data.fsobjmodel_kind.ToString()) |> ignore

            match data.fsobjmodel_kind with
            | FSharpTyconKind.TFSharpUnion ->
                data.fsobjmodel_cases.UnionCasesAsList
                |> List.iter (fun case ->
                    sb.Append("|case:") |> ignore
                    sb.Append(case.LogicalName) |> ignore
                    case.FieldTable.FieldsByIndex
                    |> Array.iter (fun field ->
                        sb.Append(":") |> ignore
                        sb.Append(field.LogicalName) |> ignore
                        sb.Append("=") |> ignore
                        sb.Append(tyToString denv field.FormalType) |> ignore))
            | FSharpTyconKind.TFSharpRecord
            | FSharpTyconKind.TFSharpStruct
            | FSharpTyconKind.TFSharpClass
            | FSharpTyconKind.TFSharpInterface
            | FSharpTyconKind.TFSharpEnum ->
                data.fsobjmodel_rfields.FieldsByIndex
                |> Array.iter (fun field ->
                    sb.Append("|field:") |> ignore
                    sb.Append(field.LogicalName) |> ignore
                    sb.Append("=") |> ignore
                    sb.Append(tyToString denv field.FormalType) |> ignore)
            | FSharpTyconKind.TFSharpDelegate slotSig ->
                sb.Append("|delegate:") |> ignore
                sb.Append(slotSig.Name) |> ignore
        | TILObjectRepr (TILObjectReprData(_, _, definition)) ->
            sb.Append("|til:") |> ignore
            sb.Append(definition.Name) |> ignore
        | TAsmRepr ilTy ->
            sb.Append("|asm:") |> ignore
            sb.Append(ilTy.ToString()) |> ignore
        | TMeasureableRepr ty ->
            sb.Append("|measure:") |> ignore
            sb.Append(tyToString denv ty) |> ignore
#if !NO_TYPEPROVIDERS
        | TProvidedTypeRepr info ->
            sb.Append("|provided:") |> ignore
            sb.Append(string info.IsErased) |> ignore
        | TProvidedNamespaceRepr _ ->
            sb.Append("|provided-namespace") |> ignore
#endif
        | TNoRepr ->
            sb.Append("|norepr") |> ignore

        sb.ToString()

    { Symbol = symbolId path tycon.LogicalName tycon.Stamp SymbolKind.Entity
      RepresentationHash = stableHash reprText
      RepresentationText = reprText }: EntitySnapshot

let private collectSnapshots denv (CheckedImplFile (qualifiedNameOfFile = qual; contents = contents)) =
    let initialPath = [ qual.Text ]
    let initialBindings: Map<string, BindingSnapshot> = Map.empty
    let initialEntities: Map<string, EntitySnapshot> = Map.empty
    snapshotModuleContents denv initialPath (initialBindings, initialEntities) contents

let private compareBindings (baseline: Map<string, BindingSnapshot>) (updated: Map<string, BindingSnapshot>) =
    let edits = ResizeArray()
    let rude = ResizeArray()

    let handleEdit symbol kind baselineHash updatedHash =
        edits.Add(
            { Symbol = symbol
              Kind = kind
              BaselineHash = baselineHash
              UpdatedHash = updatedHash }
        )

    for KeyValue(key, baselineBinding) in baseline do
        match Map.tryFind key updated with
        | Some updatedBinding ->
            if baselineBinding.SignatureText <> updatedBinding.SignatureText then
                rude.Add(
                    { Symbol = Some baselineBinding.Symbol
                      Kind = RudeEditKind.SignatureChange
                      Message =
                        $"Signature changed from '{baselineBinding.SignatureText}' to '{updatedBinding.SignatureText}'." }
                )
            elif baselineBinding.InlineInfo <> updatedBinding.InlineInfo then
                rude.Add(
                    { Symbol = Some baselineBinding.Symbol
                      Kind = RudeEditKind.InlineChange
                      Message = "Inline annotation changed." }
                )
            elif baselineBinding.BodyHash <> updatedBinding.BodyHash then
                handleEdit baselineBinding.Symbol SemanticEditKind.MethodBody (Some baselineBinding.BodyHash) (Some updatedBinding.BodyHash)
        | None ->
            rude.Add(
                { Symbol = Some baselineBinding.Symbol
                  Kind = RudeEditKind.DeclarationRemoved
                  Message = "Declaration removed." }
            )

    for KeyValue(key, updatedBinding) in updated do
        if not (Map.containsKey key baseline) then
            rude.Add(
                { Symbol = Some updatedBinding.Symbol
                  Kind = RudeEditKind.DeclarationAdded
                  Message = "New declaration added." }
            )

    edits |> Seq.toList, rude |> Seq.toList

let private compareEntities (baseline: Map<string, EntitySnapshot>) (updated: Map<string, EntitySnapshot>) =
    let rude = ResizeArray()

    for KeyValue(key, baselineEntity) in baseline do
        match Map.tryFind key updated with
        | Some updatedEntity ->
            if baselineEntity.RepresentationHash <> updatedEntity.RepresentationHash then
                rude.Add(
                    { Symbol = Some baselineEntity.Symbol
                      Kind = RudeEditKind.TypeLayoutChange
                      Message =
                        $"Type representation changed from '{baselineEntity.RepresentationText}' to '{updatedEntity.RepresentationText}'." }
                )
        | None ->
            rude.Add(
                { Symbol = Some baselineEntity.Symbol
                  Kind = RudeEditKind.DeclarationRemoved
                  Message = "Type declaration removed." }
            )

    for KeyValue(key, updatedEntity) in updated do
        if not (Map.containsKey key baseline) then
            rude.Add(
                { Symbol = Some updatedEntity.Symbol
                  Kind = RudeEditKind.DeclarationAdded
                  Message = "Type declaration added." }
            )

    rude |> Seq.toList

/// Computes semantic edits between two checked implementation files.
let diffImplementationFile (g: TcGlobals) baseline updated =
    let denv = DisplayEnv.Empty g
    let baselineBindings, baselineEntities = collectSnapshots denv baseline
    let updatedBindings, updatedEntities = collectSnapshots denv updated

    let semanticEdits, bindingRudeEdits = compareBindings baselineBindings updatedBindings
    let entityRudeEdits = compareEntities baselineEntities updatedEntities

    { SemanticEdits = semanticEdits
      RudeEdits = bindingRudeEdits @ entityRudeEdits }
