module internal FSharp.Compiler.HotReloadState

open FSharp.Compiler.HotReloadBaseline

let mutable private baseline: FSharpEmitBaseline voption = ValueNone

let setBaseline (value: FSharpEmitBaseline) = baseline <- ValueSome value

let clearBaseline () = baseline <- ValueNone

let tryGetBaseline () = baseline
