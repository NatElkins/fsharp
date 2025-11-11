module internal FSharp.Compiler.CodeGen.DeltaMetadataTypes

open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams

/// Minimal shared types for hot-reload metadata tables.
type RowElementData =
    { Tag: int
      Value: int }

type MethodDefinitionRowInfo =
    { Key: MethodDefinitionKey
      RowId: int
      IsAdded: bool
      Attributes: MethodAttributes
      ImplAttributes: MethodImplAttributes
      Name: string
      Signature: byte[]
      FirstParameterRowId: int option }

type ParameterDefinitionRowInfo =
    { Key: ParameterDefinitionKey
      RowId: int
      IsAdded: bool
      Attributes: ParameterAttributes
      SequenceNumber: int
      Name: string option }

type PropertyDefinitionRowInfo =
    { Key: PropertyDefinitionKey
      RowId: int
      IsAdded: bool
      Name: string
      Signature: byte[]
      Attributes: PropertyAttributes }

type EventDefinitionRowInfo =
    { Key: EventDefinitionKey
      RowId: int
      IsAdded: bool
      Name: string
      Attributes: EventAttributes
      EventType: EntityHandle }

type PropertyMapRowInfo =
    { DeclaringType: string
      RowId: int
      TypeDefRowId: int
      FirstPropertyRowId: int option
      IsAdded: bool }

type EventMapRowInfo =
    { DeclaringType: string
      RowId: int
      TypeDefRowId: int
      FirstEventRowId: int option
      IsAdded: bool }
