module internal FSharp.Compiler.GeneratedNames

open FSharp.Compiler.Syntax.PrettyNaming

/// Generates a hot reload compatible name with the pattern: baseName@hotreload or baseName@hotreload-N
let makeHotReloadName (baseName: string) ordinal =
    let suffix =
        if ordinal <= 0 then
            "hotreload"
        else
            sprintf "hotreload-%d" ordinal

    CompilerGeneratedNameSuffix baseName suffix
