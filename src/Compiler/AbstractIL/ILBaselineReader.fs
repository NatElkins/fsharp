/// Minimal binary reader for baseline metadata extraction.
/// Replaces SRM MetadataReader dependency for hot reload baseline creation.
/// Parses PE/CLI metadata headers to extract heap sizes and table row counts.
///
/// This module provides a pure F# implementation for reading the minimum metadata
/// needed to create an FSharpEmitBaseline, without requiring System.Reflection.Metadata.
///
/// References:
/// - ECMA-335 II.24 (Metadata physical layout)
/// - Roslyn DeltaMetadataWriter.cs for heap offset handling
module internal FSharp.Compiler.AbstractIL.ILBaselineReader

open System
open FSharp.Compiler.AbstractIL.ILBinaryWriter

/// Read a little-endian 16-bit integer from bytes at offset.
let private readUInt16 (bytes: byte[]) (offset: int) =
    uint16 bytes.[offset] ||| (uint16 bytes.[offset + 1] <<< 8)

/// Read a little-endian 32-bit integer from bytes at offset.
let private readInt32 (bytes: byte[]) (offset: int) =
    int bytes.[offset]
    ||| (int bytes.[offset + 1] <<< 8)
    ||| (int bytes.[offset + 2] <<< 16)
    ||| (int bytes.[offset + 3] <<< 24)

/// Read a little-endian 64-bit integer from bytes at offset.
let private readInt64 (bytes: byte[]) (offset: int) =
    int64 (readInt32 bytes offset) ||| (int64 (readInt32 bytes (offset + 4)) <<< 32)

/// Number of metadata tables per ECMA-335.
let private tableCount = 64

/// Find the CLI metadata root in PE file bytes.
/// Returns the offset to the metadata root, or None if not found.
let private findMetadataRoot (bytes: byte[]) : int option =
    // Check DOS header magic
    if bytes.Length < 64 || bytes.[0] <> 0x4Duy || bytes.[1] <> 0x5Auy then
        None
    else
        // e_lfanew at offset 0x3C points to PE signature
        let peOffset = readInt32 bytes 0x3C
        if peOffset < 0 || peOffset + 24 > bytes.Length then
            None
        else
            // Check PE signature "PE\0\0"
            if bytes.[peOffset] <> 0x50uy || bytes.[peOffset+1] <> 0x45uy
               || bytes.[peOffset+2] <> 0uy || bytes.[peOffset+3] <> 0uy then
                None
            else
                // COFF header at peOffset + 4
                let coffHeader = peOffset + 4
                let sizeOfOptionalHeader = int (readUInt16 bytes (coffHeader + 16))
                let optionalHeader = coffHeader + 20

                // PE32 vs PE32+ - check magic
                let magic = readUInt16 bytes optionalHeader
                let isPE32Plus = magic = 0x20Bus

                // CLI header RVA is in data directory entry 14 (0-indexed)
                // PE32: starts at optionalHeader + 96; PE32+: starts at optionalHeader + 112
                let dataDirectoryStart =
                    if isPE32Plus then optionalHeader + 112
                    else optionalHeader + 96

                let cliHeaderRVA = readInt32 bytes (dataDirectoryStart + 14 * 8)

                if cliHeaderRVA = 0 then
                    None
                else
                    // Convert RVA to file offset using section headers
                    let numberOfSections = int (readUInt16 bytes (coffHeader + 2))
                    let sectionHeadersStart = optionalHeader + sizeOfOptionalHeader

                    let rec findSection sectionIndex =
                        if sectionIndex >= numberOfSections then
                            None
                        else
                            let sectionOffset = sectionHeadersStart + sectionIndex * 40
                            let virtualAddress = readInt32 bytes (sectionOffset + 12)
                            let virtualSize = readInt32 bytes (sectionOffset + 8)
                            let pointerToRawData = readInt32 bytes (sectionOffset + 20)

                            if cliHeaderRVA >= virtualAddress && cliHeaderRVA < virtualAddress + virtualSize then
                                let cliHeaderOffset = cliHeaderRVA - virtualAddress + pointerToRawData
                                // CLI header contains MetaData RVA at offset 8
                                let metadataRVA = readInt32 bytes (cliHeaderOffset + 8)
                                // Convert metadata RVA to file offset
                                Some (metadataRVA - virtualAddress + pointerToRawData)
                            else
                                findSection (sectionIndex + 1)

                    findSection 0

/// Stream header information.
type private StreamHeader =
    { Offset: int
      Size: int
      Name: string }

/// Parse stream headers from metadata root.
let private parseStreamHeaders (bytes: byte[]) (metadataRoot: int) : StreamHeader list =
    // Metadata root signature at offset 0
    let signature = readInt32 bytes metadataRoot
    if signature <> 0x424A5342 then // "BSJB"
        []
    else
        // Version string length at offset 12
        let versionLength = readInt32 bytes (metadataRoot + 12)
        let paddedVersionLength = (versionLength + 3) &&& ~~~3

        // Number of streams at offset 16 + paddedVersionLength + 2
        let streamsOffset = metadataRoot + 16 + paddedVersionLength
        let numberOfStreams = int (readUInt16 bytes (streamsOffset + 2))

        // Stream headers start at streamsOffset + 4
        let mutable currentOffset = streamsOffset + 4
        let headers = ResizeArray<StreamHeader>()

        for _ in 1..numberOfStreams do
            let offset = readInt32 bytes currentOffset
            let size = readInt32 bytes (currentOffset + 4)

            // Read null-terminated stream name (padded to 4-byte boundary)
            let mutable nameEnd = currentOffset + 8
            while bytes.[nameEnd] <> 0uy do
                nameEnd <- nameEnd + 1
            let name = System.Text.Encoding.ASCII.GetString(bytes, currentOffset + 8, nameEnd - currentOffset - 8)
            let paddedNameLength = ((nameEnd - currentOffset - 8 + 1) + 3) &&& ~~~3

            headers.Add({ Offset = metadataRoot + offset; Size = size; Name = name })
            currentOffset <- currentOffset + 8 + paddedNameLength

        headers |> Seq.toList

/// Find a stream by name.
let private findStream (headers: StreamHeader list) (name: string) : StreamHeader option =
    headers |> List.tryFind (fun h -> h.Name = name)

/// Parse table row counts from the #~ or #- stream.
/// Returns (heapSizes byte, table row counts array, tables stream offset).
let private parseTablesStream (bytes: byte[]) (tablesStream: StreamHeader) : byte * int[] * int =
    let offset = tablesStream.Offset

    // Header structure:
    // 0-3: Reserved (0)
    // 4: MajorVersion
    // 5: MinorVersion
    // 6: HeapSizes byte
    // 7: Reserved
    // 8-15: Valid (bitmask of present tables)
    // 16-23: Sorted (bitmask of sorted tables)
    // 24+: Row counts for present tables

    let heapSizes = bytes.[offset + 6]
    let valid = readInt64 bytes (offset + 8)

    let rowCounts = Array.zeroCreate tableCount
    let mutable rowCountOffset = offset + 24

    for i in 0..63 do
        if (valid &&& (1L <<< i)) <> 0L then
            rowCounts.[i] <- readInt32 bytes rowCountOffset
            rowCountOffset <- rowCountOffset + 4

    heapSizes, rowCounts, offset

/// Extract metadata snapshot from PE file bytes.
/// This replaces metadataSnapshotFromReader for hot reload baseline creation.
let metadataSnapshotFromBytes (bytes: byte[]) : MetadataSnapshot option =
    match findMetadataRoot bytes with
    | None -> None
    | Some metadataRoot ->
        let streamHeaders = parseStreamHeaders bytes metadataRoot

        // Find required streams
        let stringsStream = findStream streamHeaders "#Strings"
        let userStringsStream = findStream streamHeaders "#US"
        let blobStream = findStream streamHeaders "#Blob"
        let guidStream = findStream streamHeaders "#GUID"
        let tablesStream =
            findStream streamHeaders "#~"
            |> Option.orElse (findStream streamHeaders "#-")

        match tablesStream with
        | None -> None
        | Some tables ->
            let _, rowCounts, _ = parseTablesStream bytes tables

            let heapSizeInfo =
                { StringHeapSize = stringsStream |> Option.map (fun s -> s.Size) |> Option.defaultValue 0
                  UserStringHeapSize = userStringsStream |> Option.map (fun s -> s.Size) |> Option.defaultValue 0
                  BlobHeapSize = blobStream |> Option.map (fun s -> s.Size) |> Option.defaultValue 0
                  GuidHeapSize = guidStream |> Option.map (fun s -> s.Size) |> Option.defaultValue 0 }

            Some
                { HeapSizes = heapSizeInfo
                  TableRowCounts = rowCounts
                  GuidHeapStart = heapSizeInfo.GuidHeapSize }

/// Read GUID from #GUID stream at 1-based index.
let readGuidFromBytes (bytes: byte[]) (guidIndex: int) : Guid option =
    if guidIndex <= 0 then
        None
    else
        match findMetadataRoot bytes with
        | None -> None
        | Some metadataRoot ->
            let streamHeaders = parseStreamHeaders bytes metadataRoot
            match findStream streamHeaders "#GUID" with
            | None -> None
            | Some guidStream ->
                // GUID indices are 1-based; each GUID is 16 bytes
                let offset = guidStream.Offset + (guidIndex - 1) * 16
                if offset + 16 > bytes.Length then
                    None
                else
                    let guidBytes = bytes.[offset..offset+15]
                    Some (System.Guid(guidBytes))

/// Read Module.Mvid GUID from assembly bytes.
/// Module table row 1 contains the Mvid index.
let readModuleMvidFromBytes (bytes: byte[]) : System.Guid option =
    match findMetadataRoot bytes with
    | None -> None
    | Some metadataRoot ->
        let streamHeaders = parseStreamHeaders bytes metadataRoot
        let tablesStreamOpt =
            findStream streamHeaders "#~"
            |> Option.orElse (findStream streamHeaders "#-")

        match tablesStreamOpt with
        | None -> None
        | Some tablesStream ->
            let heapSizes, rowCounts, tablesOffset = parseTablesStream bytes tablesStream

            // Check if Module table has at least 1 row
            if rowCounts.[0] < 1 then
                None
            else
                // Calculate offset to Module row
                // Module row structure: Generation (2), Name (string), Mvid (guid), EncId (guid), EncBaseId (guid)
                let stringsBig = (heapSizes &&& 0x01uy) <> 0uy
                let guidsBig = (heapSizes &&& 0x02uy) <> 0uy

                let stringIndexSize = if stringsBig then 4 else 2

                // Row counts end, then rows start
                let mutable rowCountSize = 0
                for i in 0..63 do
                    if rowCounts.[i] > 0 then
                        rowCountSize <- rowCountSize + 4

                let tablesStart = tablesOffset + 24 + rowCountSize

                // Module table is table 0, so it starts at tablesStart
                // Module row: Generation (2) + Name (string index) + Mvid (guid index) + EncId (guid index) + EncBaseId (guid index)
                let mvidOffset = tablesStart + 2 + stringIndexSize

                let mvidIndex =
                    if guidsBig then
                        readInt32 bytes mvidOffset
                    else
                        int (readUInt16 bytes mvidOffset)

                readGuidFromBytes bytes mvidIndex
