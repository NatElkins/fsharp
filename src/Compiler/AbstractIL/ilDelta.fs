module internal FSharp.Compiler.AbstractIL.ILDelta

open System.Reflection.Metadata.Ecma335

let private tokenRow (token: int) = token &&& 0x00FFFFFF

let buildEncTables (typeTokens: int list) (methodTokens: int list) =
    let encLog =
        [
            for token in typeTokens -> (TableIndex.TypeDef, tokenRow token, EditAndContinueOperation.Default)
            for token in methodTokens -> (TableIndex.MethodDef, tokenRow token, EditAndContinueOperation.Default)
        ]
        |> Array.ofList

    let encMap =
        [
            for token in typeTokens -> (TableIndex.TypeDef, tokenRow token)
            for token in methodTokens -> (TableIndex.MethodDef, tokenRow token)
        ]
        |> Array.ofList

    encLog, encMap
