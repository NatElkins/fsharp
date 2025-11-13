module internal FSharp.Compiler.CodeGen.DeltaMetadataTypes

open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams

/// Minimal shared types for hot-reload metadata tables.
type RowElementData =
    { Tag: int
      Value: int
      IsAbsolute: bool }

type MethodDefinitionRowInfo =
    { Key: MethodDefinitionKey
      RowId: int
      IsAdded: bool
      Attributes: MethodAttributes
      ImplAttributes: MethodImplAttributes
      Name: string
      NameHandle: StringHandle option
      Signature: byte[]
      SignatureHandle: BlobHandle option
      FirstParameterRowId: int option }

type ParameterDefinitionRowInfo =
    { Key: ParameterDefinitionKey
      RowId: int
      IsAdded: bool
      Attributes: ParameterAttributes
      SequenceNumber: int
      Name: string option
      NameHandle: StringHandle option }

type PropertyDefinitionRowInfo =
    { Key: PropertyDefinitionKey
      RowId: int
      IsAdded: bool
      Name: string
      NameHandle: StringHandle option
      Signature: byte[]
      SignatureHandle: BlobHandle option
      Attributes: PropertyAttributes }

type EventDefinitionRowInfo =
    { Key: EventDefinitionKey
      RowId: int
      IsAdded: bool
      Name: string
      NameHandle: StringHandle option
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

type MethodSemanticsMetadataUpdate =
    { RowId: int
      Association: EntityHandle
      MethodToken: int
      Attributes: MethodSemanticsAttributes
      IsAdded: bool
      AssociationInfo: MethodSemanticsAssociation option }

type TableRows =
    { Module: RowElementData[][]
      MethodDef: RowElementData[][]
      Param: RowElementData[][]
      Property: RowElementData[][]
      Event: RowElementData[][]
      PropertyMap: RowElementData[][]
      EventMap: RowElementData[][]
      MethodSemantics: RowElementData[][]
      EncLog: RowElementData[][]
      EncMap: RowElementData[][] }
