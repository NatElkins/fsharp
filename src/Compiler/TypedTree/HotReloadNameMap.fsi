module internal FSharp.Compiler.HotReloadNameMap

open System.Collections.Generic

type HotReloadNameMap =
    new: unit -> HotReloadNameMap
    member BeginSession: unit -> unit
    member GetOrAddName: basicName: string -> string
    member Snapshot: seq<string * string[]>

val nextName: HotReloadNameMap option -> basicName: string -> generate: (unit -> string) -> string
