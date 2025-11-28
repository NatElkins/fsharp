module internal FSharp.Compiler.CodeGen.DeltaTableLayout

open FSharp.Compiler.AbstractIL.ILDeltaHandles

type TableBitMasks =
    { ValidLow: int
      ValidHigh: int
      SortedLow: int
      SortedHigh: int }

let private sortedTypeSystemTables =
    [ DeltaTokens.tableInterfaceImpl
      DeltaTokens.tableConstant
      DeltaTokens.tableCustomAttribute
      DeltaTokens.tableFieldMarshal
      DeltaTokens.tableDeclSecurity
      DeltaTokens.tableClassLayout
      DeltaTokens.tableFieldLayout
      DeltaTokens.tableMethodSemantics
      DeltaTokens.tableMethodImpl
      DeltaTokens.tableImplMap
      DeltaTokens.tableFieldRVA
      DeltaTokens.tableNestedClass
      DeltaTokens.tableGenericParam
      DeltaTokens.tableGenericParamConstraint ]

let private sortedDebugTables =
    [ DeltaTokens.tableLocalScope
      DeltaTokens.tableStateMachineMethod
      DeltaTokens.tableCustomDebugInformation ]

let private maskForTables (tables: int list) =
    tables
    |> List.fold
        (fun acc tableIndex ->
            acc ||| (1UL <<< tableIndex))
        0UL

let private sortedTypeSystemMask = maskForTables sortedTypeSystemTables
let private sortedDebugMask = maskForTables sortedDebugTables

let private toLow (mask: uint64) = int (mask &&& 0xFFFFFFFFUL)
let private toHigh (mask: uint64) = int ((mask >>> 32) &&& 0xFFFFFFFFUL)

let computeBitMasks (tableRowCounts: int[]) (isEncDelta: bool) : TableBitMasks =
    let presentMask =
        tableRowCounts
        |> Array.mapi (fun index count -> if count <> 0 then 1UL <<< index else 0UL)
        |> Array.fold (|||) 0UL

    let typeSystemMask =
        if isEncDelta then
            // Roslyn clears CustomAttribute for EnC deltas to mirror MetadataSizes.
            sortedTypeSystemMask &&& ~~~(1UL <<< DeltaTokens.tableCustomAttribute)
        else
            sortedTypeSystemMask

    let sortedMask = typeSystemMask ||| (presentMask &&& sortedDebugMask)

    { ValidLow = toLow presentMask
      ValidHigh = toHigh presentMask
      SortedLow = toLow sortedMask
      SortedHigh = toHigh sortedMask }
