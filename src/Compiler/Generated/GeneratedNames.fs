module internal FSharp.Compiler.GeneratedNames

open FSharp.Compiler.Syntax.PrettyNaming

/// Minimal abstraction for compiler-generated name replay/state.
/// Implementations can be hot-reload aware without coupling core compiler paths
/// to a concrete synthesized-name map type.
type ICompilerGeneratedNameMap =
    abstract BeginSession: unit -> unit
    abstract GetOrAddName: basicName: string -> string
    abstract Snapshot: seq<struct (string * string[])>
    abstract LoadSnapshot: snapshot: seq<struct (string * string[])> -> unit

/// Generates a hot reload compatible name with the pattern: baseName@hotreload or baseName@hotreload-N
let makeHotReloadName (baseName: string) ordinal =
    let suffix =
        if ordinal <= 0 then
            "hotreload"
        else
            $"hotreload-{ordinal}"

    CompilerGeneratedNameSuffix baseName suffix
