module internal FSharp.Compiler.CodeGen.DeltaTableLayout

open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

type TableBitMasks =
    { ValidLow: int
      ValidHigh: int
      SortedLow: int
      SortedHigh: int }

let private sortedMaskLowBase = 0x3301fa00
let private sortedMaskHighBase = 0x00000200

let private sortedMaskHighExtras (tableRowCounts: int[]) =
    let hasGenericParam = tableRowCounts.[int TableIndex.GenericParam] <> 0
    let hasGenericParamConstraint = tableRowCounts.[int TableIndex.GenericParamConstraint] <> 0

    let mutable mask = sortedMaskHighBase
    if hasGenericParam then
        mask <- mask ||| 0x00000400

    if hasGenericParamConstraint then
        mask <- mask ||| 0x00001000

    mask

let computeBitMasks (tableRowCounts: int[]) : TableBitMasks =
    let mutable validLow = 0
    let mutable validHigh = 0

    for tableIndex = 0 to tableRowCounts.Length - 1 do
        if tableRowCounts.[tableIndex] <> 0 then
            if tableIndex < 32 then
                validLow <- validLow ||| (1 <<< tableIndex)
            else
                validHigh <- validHigh ||| (1 <<< (tableIndex - 32))

    { ValidLow = validLow
      ValidHigh = validHigh
      SortedLow = sortedMaskLowBase
      SortedHigh = sortedMaskHighExtras tableRowCounts }
