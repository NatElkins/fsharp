module internal FSharp.Compiler.SynthesizedTypeMaps

open System.Collections.Generic

type FSharpSynthesizedTypeMaps =
    new: unit -> FSharpSynthesizedTypeMaps
    member BeginSession: unit -> unit
    member GetOrAddName: basicName: string -> string
    member Snapshot: seq<string * string[]>

val nextName: FSharpSynthesizedTypeMaps option -> basicName: string -> generate: (unit -> string) -> string
