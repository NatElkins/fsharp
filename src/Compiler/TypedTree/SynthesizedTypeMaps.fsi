module internal FSharp.Compiler.SynthesizedTypeMaps

open System.Collections.Generic

type FSharpSynthesizedTypeMaps =
    interface FSharp.Compiler.GeneratedNames.ICompilerGeneratedNameMap
    new: unit -> FSharpSynthesizedTypeMaps
    member BeginSession: unit -> unit
    member GetOrAddName: basicName: string -> string
    member Snapshot: seq<struct (string * string[])>
    member LoadSnapshot: snapshot: seq<struct (string * string[])> -> unit

val nextName:
    FSharp.Compiler.GeneratedNames.ICompilerGeneratedNameMap option ->
        basicName: string ->
        generate: (unit -> string) ->
        string
