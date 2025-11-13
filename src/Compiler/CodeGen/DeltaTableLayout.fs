module internal FSharp.Compiler.CodeGen.DeltaTableLayout

open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

type TableBitMasks =
    { ValidLow: int
      ValidHigh: int
      SortedLow: int
      SortedHigh: int }

let private sortedTypeSystemTables =
    [ TableIndex.InterfaceImpl
      TableIndex.Constant
      TableIndex.CustomAttribute
      TableIndex.FieldMarshal
      TableIndex.DeclSecurity
      TableIndex.ClassLayout
      TableIndex.FieldLayout
      TableIndex.MethodSemantics
      TableIndex.MethodImpl
      TableIndex.ImplMap
      TableIndex.FieldRva
      TableIndex.NestedClass
      TableIndex.GenericParam
      TableIndex.GenericParamConstraint ]

let private sortedDebugTables =
    [ TableIndex.LocalScope
      TableIndex.StateMachineMethod
      TableIndex.CustomDebugInformation ]

let private maskForTables (tables: TableIndex list) =
    tables
    |> List.fold
        (fun acc tableIndex ->
            acc ||| (1UL <<< int tableIndex))
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
            sortedTypeSystemMask &&& ~~~(1UL <<< int TableIndex.CustomAttribute)
        else
            sortedTypeSystemMask

    let sortedMask = typeSystemMask ||| (presentMask &&& sortedDebugMask)

    { ValidLow = toLow presentMask
      ValidHigh = toHigh presentMask
      SortedLow = toLow sortedMask
      SortedHigh = toHigh sortedMask }
