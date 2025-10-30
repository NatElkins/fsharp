module internal FSharp.Compiler.HotReloadBaseline

open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter

type MethodDefinitionKey =
    { DeclaringType: string
      Name: string
      GenericArity: int
      ParameterTypes: ILType list
      ReturnType: ILType }

type FieldDefinitionKey =
    { DeclaringType: string
      Name: string
      FieldType: ILType }

type PropertyDefinitionKey =
    { DeclaringType: string
      Name: string
      PropertyType: ILType
      IndexParameterTypes: ILType list }

type EventDefinitionKey =
    { DeclaringType: string
      Name: string
      EventType: ILType option }

type FSharpEmitBaseline =
    { Metadata: MetadataSnapshot
      TokenMappings: ILTokenMappings
      TypeTokens: Map<string, int>
      MethodTokens: Map<MethodDefinitionKey, int>
      FieldTokens: Map<FieldDefinitionKey, int>
      PropertyTokens: Map<PropertyDefinitionKey, int>
      EventTokens: Map<EventDefinitionKey, int> }

val create: ilModule: ILModuleDef -> tokenMappings: ILTokenMappings -> metadataSnapshot: MetadataSnapshot -> FSharpEmitBaseline
