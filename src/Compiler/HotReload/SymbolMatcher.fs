module internal FSharp.Compiler.HotReload.SymbolMatcher

open System.Collections.Generic
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.HotReloadBaseline

type internal TypeMatch =
    { EnclosingTypes: ILTypeDef list
      TypeDef: ILTypeDef }

type internal MethodMatch =
    { EnclosingTypes: ILTypeDef list
      TypeDef: ILTypeDef
      MethodDef: ILMethodDef }

type FSharpSymbolMatcher =
    {
        TypeMatches: IReadOnlyDictionary<string, TypeMatch>
        MethodMatches: IReadOnlyDictionary<MethodDefinitionKey, MethodMatch>
    }

module FSharpSymbolMatcher =

    let private addMethodMatch
        (typeRef: ILTypeRef)
        (enclosing: ILTypeDef list)
        (typeDef: ILTypeDef)
        (methodDef: ILMethodDef)
        (destination: Dictionary<MethodDefinitionKey, MethodMatch>)
        =
        let key =
            { DeclaringType = typeRef.FullName
              Name = methodDef.Name
              GenericArity = methodDef.GenericParams.Length
              ParameterTypes = methodDef.ParameterTypes
              ReturnType = methodDef.Return.Type }

        destination[key] <-
            { EnclosingTypes = enclosing
              TypeDef = typeDef
              MethodDef = methodDef }

    let rec private addTypeMatches
        (enclosing: ILTypeDef list)
        (types: Dictionary<string, TypeMatch>)
        (methods: Dictionary<MethodDefinitionKey, MethodMatch>)
        (typeDef: ILTypeDef)
        =
        let typeRef = mkRefForNestedILTypeDef ILScopeRef.Local (enclosing, typeDef)
        types[typeRef.FullName] <-
            { EnclosingTypes = enclosing
              TypeDef = typeDef }

        typeDef.Methods.AsList()
        |> List.iter (fun methodDef ->
            addMethodMatch typeRef enclosing typeDef methodDef methods)

        typeDef.NestedTypes.AsList()
        |> List.iter (fun nested -> addTypeMatches (enclosing @ [ typeDef ]) types methods nested)

    let create (moduleDef: ILModuleDef) : FSharpSymbolMatcher =
        let typeMatches = Dictionary<string, TypeMatch>()
        let methodMatches = Dictionary<MethodDefinitionKey, MethodMatch>()

        moduleDef.TypeDefs.AsList()
        |> List.iter (addTypeMatches [] typeMatches methodMatches)

        { TypeMatches = typeMatches :> IReadOnlyDictionary<string, TypeMatch>
          MethodMatches = methodMatches :> IReadOnlyDictionary<MethodDefinitionKey, MethodMatch> }

    let tryGetTypeDef (matcher: FSharpSymbolMatcher) (fullName: string) =
        match matcher.TypeMatches.TryGetValue fullName with
        | true, matchInfo -> Some(matchInfo.EnclosingTypes, matchInfo.TypeDef)
        | _ -> None

    let tryGetMethodDef (matcher: FSharpSymbolMatcher) (key: MethodDefinitionKey) =
        match matcher.MethodMatches.TryGetValue key with
        | true, matchInfo -> Some(matchInfo.EnclosingTypes, matchInfo.TypeDef, matchInfo.MethodDef)
        | _ -> None
