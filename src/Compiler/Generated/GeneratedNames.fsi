module internal FSharp.Compiler.GeneratedNames

/// Generates a hot reload compatible name with the pattern: baseName@hotreload or baseName@hotreload-N
val makeHotReloadName: baseName: string -> ordinal: int -> string
