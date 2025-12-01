module internal FSharp.Compiler.HotReloadBaseline

open System
open System.Collections.Immutable
open System.Reflection
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILDeltaHandles
open FSharp.Compiler.IlxGen

/// <summary>Stable identifier for a method definition used when correlating baseline tokens.</summary>
type MethodDefinitionKey =
    { DeclaringType: string
      Name: string
      GenericArity: int
      ParameterTypes: ILType list
      ReturnType: ILType }


type ParameterDefinitionKey =
    { Method: MethodDefinitionKey
      SequenceNumber: int }

type TypeReferenceKey =
    { Scope: string
      Namespace: string
      Name: string }

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

type MethodDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      SignatureOffset: BlobOffset option
      FirstParameterRowId: int option
      Rva: int option
      Attributes: MethodAttributes option
      ImplAttributes: MethodImplAttributes option }

type ParameterDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      RowId: int option }

type PropertyDefinitionMetadataHandles =
    { NameOffset: StringOffset option
      SignatureOffset: BlobOffset option }

type EventDefinitionMetadataHandles = { NameOffset: StringOffset option }

type BaselineHandleCache =
    { MethodHandles: Map<MethodDefinitionKey, MethodDefinitionMetadataHandles>
      ParameterHandles: Map<ParameterDefinitionKey, ParameterDefinitionMetadataHandles>
      PropertyHandles: Map<PropertyDefinitionKey, PropertyDefinitionMetadataHandles>
      EventHandles: Map<EventDefinitionKey, EventDefinitionMetadataHandles> }

type MethodSemanticsAssociation =
    | PropertyAssociation of PropertyDefinitionKey * rowId:int
    | EventAssociation of EventDefinitionKey * rowId:int

type MethodSemanticsEntry =
    { RowId: int
      Attributes: MethodSemanticsAttributes
      Association: MethodSemanticsAssociation }

/// <summary>Portable PDB snapshot captured during baseline emission.</summary>
type PortablePdbSnapshot =
    { Bytes: byte[]
      TableRowCounts: ImmutableArray<int>
      EntryPointToken: int option }

type AddedOrChangedMethodInfo =
    { MethodToken: int
      LocalSignatureToken: int
      CodeOffset: int
      CodeLength: int }

/// <summary>
/// Represents the captured state of a baseline emission, mirroring Roslyn's EmitBaseline. It stores metadata
/// snapshots along with stable token maps so delta emission can reuse pre-existing metadata handles.
/// </summary>
type FSharpEmitBaseline =
    { ModuleId: Guid
      EncId: Guid
      EncBaseId: Guid
      NextGeneration: int
      ModuleNameOffset: StringOffset option
      Metadata: MetadataSnapshot
      TokenMappings: ILTokenMappings
      TypeTokens: Map<string, int>
      MethodTokens: Map<MethodDefinitionKey, int>
      FieldTokens: Map<FieldDefinitionKey, int>
      PropertyTokens: Map<PropertyDefinitionKey, int>
      EventTokens: Map<EventDefinitionKey, int>
      PropertyMapEntries: Map<string, int>
      EventMapEntries: Map<string, int>
      MethodSemanticsEntries: Map<MethodDefinitionKey, MethodSemanticsEntry list>
      IlxGenEnvironment: IlxGenEnvSnapshot option
      PortablePdb: PortablePdbSnapshot option
      SynthesizedNameSnapshot: Map<string, string[]>
      MetadataHandles: BaselineHandleCache
      TypeReferenceTokens: Map<TypeReferenceKey, int>
      AssemblyReferenceTokens: Map<string, int>
      TableEntriesAdded: int[]
      StringStreamLengthAdded: int
      UserStringStreamLengthAdded: int
      BlobStreamLengthAdded: int
      GuidStreamLengthAdded: int
      AddedOrChangedMethods: AddedOrChangedMethodInfo list }

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

/// Extract metadata snapshot from PE file bytes without using SRM.
val metadataSnapshotFromBytes: bytes: byte[] -> MetadataSnapshot option

/// Read Module.Mvid GUID from PE file bytes without using SRM.
val readModuleMvid: bytes: byte[] -> System.Guid option

/// Attach metadata handles from PE bytes without using SRM MetadataReader.
val attachMetadataHandlesFromBytes: bytes: byte[] -> baseline: FSharpEmitBaseline -> FSharpEmitBaseline

val applyDelta:
    baseline: FSharpEmitBaseline ->
    deltaTableCounts: int[] ->
    deltaHeapSizes: MetadataHeapSizes ->
    addedOrChangedMethods: AddedOrChangedMethodInfo list ->
    encId: Guid ->
    encBaseId: Guid ->
    synthesizedSnapshot: Map<string, string[]> option ->
        FSharpEmitBaseline

val collectMethodSemanticsEntries :
    ilModule: ILModuleDef ->
    methodTokens: Map<MethodDefinitionKey, int> ->
    propertyTokens: Map<PropertyDefinitionKey, int> ->
    eventTokens: Map<EventDefinitionKey, int> ->
        Map<MethodDefinitionKey, MethodSemanticsEntry list>
