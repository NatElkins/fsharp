namespace FSharp.Compiler.Service.Tests.HotReload

open System.Reflection.Metadata.Ecma335
open Xunit

open FSharp.Compiler.CodeGen.DeltaIndexSizing
open FSharp.Compiler.AbstractIL.ILBinaryWriter

/// Tests for edge cases in hot reload infrastructure.
/// These tests validate behavior at boundary conditions like large row counts,
/// heap size thresholds, and index size transitions.
module EdgeCaseTests =

    /// Helper to create heap sizes
    let private createHeapSizes string userString blob guid =
        { StringHeapSize = string
          UserStringHeapSize = userString
          BlobHeapSize = blob
          GuidHeapSize = guid }

    /// Helper to create table row counts with specific values
    let private createTableRowCounts (entries: (TableIndex * int) list) =
        let counts = Array.zeroCreate 64
        for (table, count) in entries do
            counts.[int table] <- count
        counts

    module IndexSizeThresholdTests =

        /// 0x10000 (65536) is the threshold where indices switch from 2 bytes to 4 bytes
        let private threshold = 0x10000

        [<Fact>]
        let ``string heap under threshold uses small index`` () =
            let heapSizes = createHeapSizes (threshold - 1) 0 0 0
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.StringsBig, "String heap under threshold should use small index")

        [<Fact>]
        let ``string heap at threshold uses big index`` () =
            let heapSizes = createHeapSizes threshold 0 0 0
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.StringsBig, "String heap at threshold should use big index")

        [<Fact>]
        let ``blob heap under threshold uses small index`` () =
            let heapSizes = createHeapSizes 0 0 (threshold - 1) 0
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.BlobsBig, "Blob heap under threshold should use small index")

        [<Fact>]
        let ``blob heap at threshold uses big index`` () =
            let heapSizes = createHeapSizes 0 0 threshold 0
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.BlobsBig, "Blob heap at threshold should use big index")

        [<Fact>]
        let ``guid heap under threshold uses small index`` () =
            let heapSizes = createHeapSizes 0 0 0 (threshold - 1)
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.GuidsBig, "GUID heap under threshold should use small index")

        [<Fact>]
        let ``guid heap at threshold uses big index`` () =
            let heapSizes = createHeapSizes 0 0 0 threshold
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.GuidsBig, "GUID heap at threshold should use big index")

    module SimpleIndexTests =

        let private threshold = 0x10000

        [<Fact>]
        let ``TypeDef table under threshold uses small index`` () =
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, threshold - 1) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.SimpleIndexBig.[int TableIndex.TypeDef], "TypeDef under threshold should use small index")

        [<Fact>]
        let ``TypeDef table at threshold uses big index`` () =
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, threshold) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.SimpleIndexBig.[int TableIndex.TypeDef], "TypeDef at threshold should use big index")

        [<Fact>]
        let ``MethodDef table under threshold uses small index`` () =
            let tableRowCounts = createTableRowCounts [ (TableIndex.MethodDef, threshold - 1) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.SimpleIndexBig.[int TableIndex.MethodDef], "MethodDef under threshold should use small index")

        [<Fact>]
        let ``MethodDef table at threshold uses big index`` () =
            let tableRowCounts = createTableRowCounts [ (TableIndex.MethodDef, threshold) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.SimpleIndexBig.[int TableIndex.MethodDef], "MethodDef at threshold should use big index")

        [<Fact>]
        let ``external row counts contribute to threshold`` () =
            // Local = 30000, External = 40000, Total = 70000 > threshold
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, 30000) ]
            let externalRowCounts = createTableRowCounts [ (TableIndex.TypeDef, 40000) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts externalRowCounts heapSizes false

            Assert.True(sizes.SimpleIndexBig.[int TableIndex.TypeDef],
                "Combined local + external rows exceeding threshold should use big index")

        [<Fact>]
        let ``external row counts under threshold use small index`` () =
            // Local = 30000, External = 30000, Total = 60000 < threshold
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, 30000) ]
            let externalRowCounts = createTableRowCounts [ (TableIndex.TypeDef, 30000) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts externalRowCounts heapSizes false

            Assert.False(sizes.SimpleIndexBig.[int TableIndex.TypeDef],
                "Combined rows under threshold should use small index")

    module CodedIndexTests =

        [<Fact>]
        let ``TypeDefOrRef with 2 tag bits has correct threshold`` () =
            // TypeDefOrRef uses 2 tag bits, so threshold is 2^(16-2) = 16384
            let codedThreshold = pown 2 (16 - 2)  // 16384
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, codedThreshold - 1) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.TypeDefOrRefBig, "TypeDefOrRef under coded threshold should use small index")

        [<Fact>]
        let ``TypeDefOrRef at coded threshold uses big index`` () =
            let codedThreshold = pown 2 (16 - 2)  // 16384
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, codedThreshold) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.TypeDefOrRefBig, "TypeDefOrRef at coded threshold should use big index")

        [<Fact>]
        let ``MemberRefParent with 3 tag bits has correct threshold`` () =
            // MemberRefParent uses 3 tag bits, so threshold is 2^(16-3) = 8192
            let codedThreshold = pown 2 (16 - 3)  // 8192
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeRef, codedThreshold - 1) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.MemberRefParentBig, "MemberRefParent under coded threshold should use small index")

        [<Fact>]
        let ``MemberRefParent at coded threshold uses big index`` () =
            let codedThreshold = pown 2 (16 - 3)  // 8192
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeRef, codedThreshold) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.MemberRefParentBig, "MemberRefParent at coded threshold should use big index")

        [<Fact>]
        let ``HasCustomAttribute with 5 tag bits has correct threshold`` () =
            // HasCustomAttribute uses 5 tag bits, so threshold is 2^(16-5) = 2048
            let codedThreshold = pown 2 (16 - 5)  // 2048
            let tableRowCounts = createTableRowCounts [ (TableIndex.MethodDef, codedThreshold - 1) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.HasCustomAttributeBig, "HasCustomAttribute under coded threshold should use small index")

        [<Fact>]
        let ``HasCustomAttribute at coded threshold uses big index`` () =
            let codedThreshold = pown 2 (16 - 5)  // 2048
            let tableRowCounts = createTableRowCounts [ (TableIndex.MethodDef, codedThreshold) ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.HasCustomAttributeBig, "HasCustomAttribute at coded threshold should use big index")

        [<Fact>]
        let ``any table in coded index group exceeding threshold triggers big index`` () =
            // TypeDefOrRef includes TypeDef, TypeRef, TypeSpec
            // If any one exceeds threshold, coded index is big
            let codedThreshold = pown 2 (16 - 2)  // 16384
            let tableRowCounts = createTableRowCounts [
                (TableIndex.TypeDef, 1)
                (TableIndex.TypeRef, 1)
                (TableIndex.TypeSpec, codedThreshold)  // Only TypeSpec exceeds
            ]
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.TypeDefOrRefBig, "Any table exceeding threshold should trigger big coded index")

    module EncDeltaTests =

        [<Fact>]
        let ``EncDelta mode forces all indices to big`` () =
            // In EnC delta mode (isEncDelta=true), all indices are big regardless of counts
            let tableRowCounts = Array.zeroCreate 64
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes true

            Assert.True(sizes.StringsBig, "EncDelta should force strings big")
            Assert.True(sizes.BlobsBig, "EncDelta should force blobs big")
            Assert.True(sizes.GuidsBig, "EncDelta should force GUIDs big")
            Assert.True(sizes.TypeDefOrRefBig, "EncDelta should force TypeDefOrRef big")
            Assert.True(sizes.MemberRefParentBig, "EncDelta should force MemberRefParent big")
            Assert.True(sizes.HasCustomAttributeBig, "EncDelta should force HasCustomAttribute big")

        [<Fact>]
        let ``EncDelta mode forces simple indices big`` () =
            let tableRowCounts = Array.zeroCreate 64
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes true

            // All simple indices should be big in EncDelta mode
            for i in 0..63 do
                Assert.True(sizes.SimpleIndexBig.[i], $"SimpleIndex[{i}] should be big in EncDelta mode")

    module BoundaryTests =

        [<Fact>]
        let ``zero row counts produce small indices`` () =
            let tableRowCounts = Array.zeroCreate 64
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.StringsBig)
            Assert.False(sizes.BlobsBig)
            Assert.False(sizes.GuidsBig)
            Assert.False(sizes.TypeDefOrRefBig)
            Assert.False(sizes.MemberRefParentBig)

        [<Fact>]
        let ``maximum heap size produces big indices`` () =
            let maxHeap = System.Int32.MaxValue
            let heapSizes = createHeapSizes maxHeap maxHeap maxHeap maxHeap
            let tableRowCounts = Array.zeroCreate 64
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.StringsBig)
            Assert.True(sizes.BlobsBig)
            Assert.True(sizes.GuidsBig)

        [<Fact>]
        let ``maximum row counts produce big indices`` () =
            let tableRowCounts = Array.create 64 System.Int32.MaxValue
            let heapSizes = createHeapSizes 0 0 0 0
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.True(sizes.TypeDefOrRefBig)
            Assert.True(sizes.MemberRefParentBig)
            Assert.True(sizes.HasCustomAttributeBig)
            Assert.True(sizes.HasDeclSecurityBig)

        [<Fact>]
        let ``exactly at threshold minus one is still small`` () =
            // Boundary condition: threshold - 1 should be small
            let threshold = 0x10000
            let tableRowCounts = createTableRowCounts [ (TableIndex.TypeDef, threshold - 1) ]
            let heapSizes = createHeapSizes (threshold - 1) 0 (threshold - 1) (threshold - 1)
            let sizes = compute tableRowCounts [||] heapSizes false

            Assert.False(sizes.StringsBig, "Exactly threshold - 1 should be small")
            Assert.False(sizes.SimpleIndexBig.[int TableIndex.TypeDef], "Row count threshold - 1 should be small")

