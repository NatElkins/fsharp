module internal FSharp.Compiler.AbstractIL.ILDelta

open System.Reflection.Metadata.Ecma335

let private tokenRow (token: int) = token &&& 0x00FFFFFF

let private encLogEntry (tableIndex: TableIndex) (token: int) =
    (tableIndex, tokenRow token, EditAndContinueOperation.Default)

let private encMapEntry (tableIndex: TableIndex) (token: int) =
    (tableIndex, tokenRow token)

/// Builds EncLog and EncMap projections for updated type/method tokens.
let buildEncTables (typeTokens: int list) (methodTokens: int list) =
    let typeLog =
        typeTokens |> List.map (encLogEntry TableIndex.TypeDef)

    let methodLog =
        methodTokens |> List.map (encLogEntry TableIndex.MethodDef)

    let encLog = Array.ofList (typeLog @ methodLog)

    let typeMap =
        typeTokens |> List.map (encMapEntry TableIndex.TypeDef)

    let methodMap =
        methodTokens |> List.map (encMapEntry TableIndex.MethodDef)

    let encMap = Array.ofList (typeMap @ methodMap)

    encLog, encMap
