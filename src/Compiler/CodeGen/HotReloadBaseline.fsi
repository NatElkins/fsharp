module internal FSharp.Compiler.HotReloadBaseline

open System
open System.Collections.Immutable
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

/// <summary>Portable PDB snapshot captured during baseline emission.</summary>
type PortablePdbSnapshot =
    { Bytes: byte[]
      TableRowCounts: ImmutableArray<int>
      EntryPointToken: int option }

/// <summary>
/// Represents the captured state of a baseline emission, mirroring Roslyn's EmitBaseline. It stores metadata
/// snapshots along with stable token maps so delta emission can reuse pre-existing metadata handles.
/// </summary>
type FSharpEmitBaseline =
    { ModuleId: Guid
      Metadata: MetadataSnapshot
      TokenMappings: ILTokenMappings
      TypeTokens: Map<string, int>
      MethodTokens: Map<MethodDefinitionKey, int>
      FieldTokens: Map<FieldDefinitionKey, int>
      PropertyTokens: Map<PropertyDefinitionKey, int>
      EventTokens: Map<EventDefinitionKey, int>
      IlxGenEnvironment: IlxGenEnvSnapshot option
      PortablePdb: PortablePdbSnapshot option }

/// <summary>Create a baseline record for the supplied IL module and token mappings.</summary>
val create:
    ilModule: ILModuleDef ->
    tokenMappings: ILTokenMappings ->
    metadataSnapshot: MetadataSnapshot ->
    moduleId: Guid ->
    portablePdbSnapshot: PortablePdbSnapshot option ->
        FSharpEmitBaseline

/// <summary>Create a baseline record that also persists the supplied ILX environment snapshot.</summary>
val createWithEnvironment:
    ilModule: ILModuleDef ->
    tokenMappings: ILTokenMappings ->
    metadataSnapshot: MetadataSnapshot ->
    ilxGenEnvironment: IlxGenEnvSnapshot ->
    moduleId: Guid ->
    portablePdbSnapshot: PortablePdbSnapshot option ->
        FSharpEmitBaseline
