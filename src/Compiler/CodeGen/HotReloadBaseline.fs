module internal FSharp.Compiler.HotReloadBaseline

open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.IlxGen

/// <summary>Stable identifier for a method definition used when correlating baseline tokens.</summary>
type MethodDefinitionKey =
    { DeclaringType: string
      Name: string
      GenericArity: int
      ParameterTypes: ILType list
      ReturnType: ILType }

/// <summary>Stable identifier for a field definition in the baseline assembly.</summary>
type FieldDefinitionKey =
    { DeclaringType: string
      Name: string
      FieldType: ILType }

/// <summary>Stable identifier for a property definition (including indexer parameter shapes).</summary>
type PropertyDefinitionKey =
    { DeclaringType: string
      Name: string
      PropertyType: ILType
      IndexParameterTypes: ILType list }

/// <summary>Stable identifier for an event definition in the baseline assembly.</summary>
type EventDefinitionKey =
    { DeclaringType: string
      Name: string
      EventType: ILType option }

/// <summary>
/// Represents the captured state of a baseline emission, mirroring Roslyn's EmitBaseline. It stores metadata
/// snapshots along with stable token maps so delta emission can reuse pre-existing metadata handles.
/// </summary>
type FSharpEmitBaseline =
    { Metadata: MetadataSnapshot
      TokenMappings: ILTokenMappings
      TypeTokens: Map<string, int>
      MethodTokens: Map<MethodDefinitionKey, int>
      FieldTokens: Map<FieldDefinitionKey, int>
      PropertyTokens: Map<PropertyDefinitionKey, int>
      EventTokens: Map<EventDefinitionKey, int>
      IlxGenEnvironment: IlxGenEnvSnapshot option }

type private BaselineMaps =
    { TypeTokens: Map<string, int>
      MethodTokens: Map<MethodDefinitionKey, int>
      FieldTokens: Map<FieldDefinitionKey, int>
      PropertyTokens: Map<PropertyDefinitionKey, int>
      EventTokens: Map<EventDefinitionKey, int> }

let private emptyMaps =
    { TypeTokens = Map.empty
      MethodTokens = Map.empty
      FieldTokens = Map.empty
      PropertyTokens = Map.empty
      EventTokens = Map.empty }

/// <summary>
/// Populate the baseline token maps by walking type definitions and their nested members.
/// </summary>
let rec private collectType
    (tokenMappings: ILTokenMappings)
    (scope: ILScopeRef)
    (enclosing: ILTypeDef list)
    (maps: BaselineMaps)
    (tdef: ILTypeDef)
    : BaselineMaps
    =
    let typeRef = mkRefForNestedILTypeDef scope (enclosing, tdef)
    let typeName = typeRef.FullName
    let typeToken = tokenMappings.TypeDefTokenMap (enclosing, tdef)

    let maps = { maps with TypeTokens = maps.TypeTokens |> Map.add typeName typeToken }

    let maps =
        tdef.Methods.AsList()
        |> List.fold (fun (acc: BaselineMaps) mdef ->
            let key =
                { DeclaringType = typeName
                  Name = mdef.Name
                  GenericArity = mdef.GenericParams.Length
                  ParameterTypes = mdef.ParameterTypes
                  ReturnType = mdef.Return.Type }

            let token = tokenMappings.MethodDefTokenMap (enclosing, tdef) mdef
            { acc with MethodTokens = acc.MethodTokens |> Map.add key token }) maps

    let maps =
        tdef.Fields.AsList()
        |> List.fold (fun (acc: BaselineMaps) fdef ->
            let key =
                { DeclaringType = typeName
                  Name = fdef.Name
                  FieldType = fdef.FieldType }

            let token = tokenMappings.FieldDefTokenMap (enclosing, tdef) fdef
            { acc with FieldTokens = acc.FieldTokens |> Map.add key token }) maps

    let maps =
        tdef.Properties.AsList()
        |> List.fold (fun (acc: BaselineMaps) pdef ->
            let key =
                { DeclaringType = typeName
                  Name = pdef.Name
                  PropertyType = pdef.PropertyType
                  IndexParameterTypes = List.ofSeq pdef.Args }

            let token = tokenMappings.PropertyTokenMap (enclosing, tdef) pdef
            { acc with PropertyTokens = acc.PropertyTokens |> Map.add key token }) maps

    let maps =
        tdef.Events.AsList()
        |> List.fold (fun (acc: BaselineMaps) edef ->
            let key =
                { DeclaringType = typeName
                  Name = edef.Name
                  EventType = edef.EventType }

            let token = tokenMappings.EventTokenMap (enclosing, tdef) edef
            { acc with EventTokens = acc.EventTokens |> Map.add key token }) maps

    tdef.NestedTypes.AsList()
    |> List.fold (collectType tokenMappings scope (enclosing @ [ tdef ])) maps

let private createCore
    (ilModule: ILModuleDef)
    (tokenMappings: ILTokenMappings)
    (metadataSnapshot: MetadataSnapshot)
    (ilxGenEnvironment: IlxGenEnvSnapshot option)
    =
    let scope = ILScopeRef.Local

    let maps =
        ilModule.TypeDefs.AsList()
        |> List.fold (collectType tokenMappings scope []) emptyMaps

    { Metadata = metadataSnapshot
      TokenMappings = tokenMappings
      TypeTokens = maps.TypeTokens
      MethodTokens = maps.MethodTokens
      FieldTokens = maps.FieldTokens
      PropertyTokens = maps.PropertyTokens
      EventTokens = maps.EventTokens
      IlxGenEnvironment = ilxGenEnvironment }

/// <summary>Create an <see cref="FSharpEmitBaseline"/> without capturing the ILX environment snapshot.</summary>
let create (ilModule: ILModuleDef) (tokenMappings: ILTokenMappings) (metadataSnapshot: MetadataSnapshot) =
    createCore ilModule tokenMappings metadataSnapshot None

/// <summary>Create an <see cref="FSharpEmitBaseline"/> that carries the captured ILX environment snapshot.</summary>
let createWithEnvironment
    (ilModule: ILModuleDef)
    (tokenMappings: ILTokenMappings)
    (metadataSnapshot: MetadataSnapshot)
    (ilxGenEnvironment: IlxGenEnvSnapshot)
    =
    createCore ilModule tokenMappings metadataSnapshot (Some ilxGenEnvironment)
