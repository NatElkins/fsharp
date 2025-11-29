namespace FSharp.Compiler.Service.Tests.HotReload

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Xunit
open FSharp.Compiler.AbstractIL.ILBaselineReader
open FSharp.Compiler.HotReloadBaseline

/// Tests for ILBaselineReader - verifies byte-based metadata parsing
/// matches SRM MetadataReader results.
module ILBaselineReaderTests =

    /// Marker type for assembly location
    type TestMarker = class end

    /// Helper to get assembly bytes from a compiled test assembly
    let private getTestAssemblyBytes () =
        // Use the current test assembly as a test subject
        let assembly = typeof<TestMarker>.Assembly
        let assemblyPath = assembly.Location
        File.ReadAllBytes(assemblyPath)

    [<Fact>]
    let ``metadataSnapshotFromBytes parses valid PE file`` () =
        let bytes = getTestAssemblyBytes ()
        let result = metadataSnapshotFromBytes bytes
        Assert.True(result.IsSome, "Should successfully parse PE file")

    [<Fact>]
    let ``metadataSnapshotFromBytes returns None for invalid bytes`` () =
        let invalidBytes = [| 0uy; 1uy; 2uy; 3uy |]
        let result = metadataSnapshotFromBytes invalidBytes
        Assert.True(result.IsNone, "Should return None for invalid PE file")

    [<Fact>]
    let ``metadataSnapshotFromBytes matches MetadataReader for heap sizes`` () =
        let bytes = getTestAssemblyBytes ()

        // Parse using our byte-based reader
        let byteResult = metadataSnapshotFromBytes bytes
        Assert.True(byteResult.IsSome)
        let byteSnapshot = byteResult.Value

        // Parse using SRM MetadataReader
        use stream = new MemoryStream(bytes)
        use peReader = new PEReader(stream)
        let metadataReader = peReader.GetMetadataReader()
        let srmSnapshot = metadataSnapshotFromReader metadataReader

        // Compare heap sizes - our parser reads raw stream sizes from headers,
        // while SRM's GetHeapSize may return content-only size (excluding padding).
        // Per ECMA-335 II.24.2.2, streams are 4-byte aligned, so we allow small tolerance.
        let heapSizeTolerance = 4

        Assert.True(
            abs(srmSnapshot.HeapSizes.StringHeapSize - byteSnapshot.HeapSizes.StringHeapSize) <= heapSizeTolerance,
            $"String heap size mismatch: SRM={srmSnapshot.HeapSizes.StringHeapSize}, byte-based={byteSnapshot.HeapSizes.StringHeapSize}")
        Assert.True(
            abs(srmSnapshot.HeapSizes.UserStringHeapSize - byteSnapshot.HeapSizes.UserStringHeapSize) <= heapSizeTolerance,
            $"UserString heap size mismatch: SRM={srmSnapshot.HeapSizes.UserStringHeapSize}, byte-based={byteSnapshot.HeapSizes.UserStringHeapSize}")
        Assert.True(
            abs(srmSnapshot.HeapSizes.BlobHeapSize - byteSnapshot.HeapSizes.BlobHeapSize) <= heapSizeTolerance,
            $"Blob heap size mismatch: SRM={srmSnapshot.HeapSizes.BlobHeapSize}, byte-based={byteSnapshot.HeapSizes.BlobHeapSize}")
        Assert.Equal(srmSnapshot.HeapSizes.GuidHeapSize, byteSnapshot.HeapSizes.GuidHeapSize)

    [<Fact>]
    let ``metadataSnapshotFromBytes matches MetadataReader for table row counts`` () =
        let bytes = getTestAssemblyBytes ()

        // Parse using our byte-based reader
        let byteResult = metadataSnapshotFromBytes bytes
        Assert.True(byteResult.IsSome)
        let byteSnapshot = byteResult.Value

        // Parse using SRM MetadataReader
        use stream = new MemoryStream(bytes)
        use peReader = new PEReader(stream)
        let metadataReader = peReader.GetMetadataReader()
        let srmSnapshot = metadataSnapshotFromReader metadataReader

        // Compare all 64 table row counts
        Assert.Equal(srmSnapshot.TableRowCounts.Length, byteSnapshot.TableRowCounts.Length)
        for i in 0..63 do
            if srmSnapshot.TableRowCounts.[i] <> byteSnapshot.TableRowCounts.[i] then
                Assert.Fail($"Table {i} row count mismatch: expected {srmSnapshot.TableRowCounts.[i]}, got {byteSnapshot.TableRowCounts.[i]}")

    [<Fact>]
    let ``readModuleMvidFromBytes returns valid GUID`` () =
        let bytes = getTestAssemblyBytes ()
        let result = readModuleMvidFromBytes bytes
        Assert.True(result.IsSome, "Should successfully read MVID")
        Assert.NotEqual(Guid.Empty, result.Value)

    [<Fact>]
    let ``readModuleMvidFromBytes matches MetadataReader`` () =
        let bytes = getTestAssemblyBytes ()

        // Read using our byte-based reader
        let byteResult = readModuleMvidFromBytes bytes
        Assert.True(byteResult.IsSome)

        // Read using SRM MetadataReader
        use stream = new MemoryStream(bytes)
        use peReader = new PEReader(stream)
        let metadataReader = peReader.GetMetadataReader()
        let moduleDef = metadataReader.GetModuleDefinition()
        let srmMvid =
            if moduleDef.Mvid.IsNil then Guid.Empty
            else metadataReader.GetGuid(moduleDef.Mvid)

        Assert.Equal(srmMvid, byteResult.Value)

    [<Fact>]
    let ``metadataSnapshotFromBytes works with delta-generated test assembly`` () =
        // Use a test helper to create a known assembly
        let artifacts = MetadataDeltaTestHelpers.emitPropertyDeltaArtifacts None ()
        let bytes = artifacts.BaselineBytes

        let byteResult = metadataSnapshotFromBytes bytes
        Assert.True(byteResult.IsSome)
        let byteSnapshot = byteResult.Value

        // Parse using SRM MetadataReader
        use stream = new MemoryStream(bytes)
        use peReader = new PEReader(stream)
        let metadataReader = peReader.GetMetadataReader()
        let srmSnapshot = metadataSnapshotFromReader metadataReader

        // Compare heap sizes - with tolerance for stream alignment
        let heapSizeTolerance = 4
        Assert.True(
            abs(srmSnapshot.HeapSizes.StringHeapSize - byteSnapshot.HeapSizes.StringHeapSize) <= heapSizeTolerance,
            $"String heap size mismatch: SRM={srmSnapshot.HeapSizes.StringHeapSize}, byte-based={byteSnapshot.HeapSizes.StringHeapSize}")
        Assert.True(
            abs(srmSnapshot.HeapSizes.UserStringHeapSize - byteSnapshot.HeapSizes.UserStringHeapSize) <= heapSizeTolerance,
            $"UserString heap size mismatch: SRM={srmSnapshot.HeapSizes.UserStringHeapSize}, byte-based={byteSnapshot.HeapSizes.UserStringHeapSize}")
        Assert.True(
            abs(srmSnapshot.HeapSizes.BlobHeapSize - byteSnapshot.HeapSizes.BlobHeapSize) <= heapSizeTolerance,
            $"Blob heap size mismatch: SRM={srmSnapshot.HeapSizes.BlobHeapSize}, byte-based={byteSnapshot.HeapSizes.BlobHeapSize}")
        Assert.Equal(srmSnapshot.HeapSizes.GuidHeapSize, byteSnapshot.HeapSizes.GuidHeapSize)

        // Compare all table row counts
        for i in 0..63 do
            if srmSnapshot.TableRowCounts.[i] <> byteSnapshot.TableRowCounts.[i] then
                Assert.Fail($"Table {i} row count mismatch: expected {srmSnapshot.TableRowCounts.[i]}, got {byteSnapshot.TableRowCounts.[i]}")
