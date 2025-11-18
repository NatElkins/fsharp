module internal FSharp.Compiler.CodeGen.DeltaMetadataTables

open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Text
open Microsoft.FSharp.Collections
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.IlxDeltaStreams
open FSharp.Compiler.CodeGen.DeltaMetadataTypes

/// Mirrors the AbstractIL metadata tables for the subset of rows emitted by
/// hot reload deltas. The tables are populated alongside the SRM metadata
/// builder so we can eventually serialize deltas directly via AbstractIL.
type MetadataHeapOffsets =
    {
        StringHeapStart: int
        BlobHeapStart: int
        GuidHeapStart: int
        UserStringHeapStart: int
    }

    static member Zero =
        { StringHeapStart = 0
          BlobHeapStart = 0
          GuidHeapStart = 0
          UserStringHeapStart = 0 }

    static member OfHeapSizes(heapSizes: MetadataHeapSizes) =
        { StringHeapStart = heapSizes.StringHeapSize
          BlobHeapStart = heapSizes.BlobHeapSize
          GuidHeapStart = heapSizes.GuidHeapSize
          UserStringHeapStart = heapSizes.UserStringHeapSize }

let private byteArrayComparer : IEqualityComparer<byte[]> =
    { new IEqualityComparer<byte[]> with
        member _.Equals(x, y) =
            if obj.ReferenceEquals(x, y) then true
            elif isNull (box x) || isNull (box y) then false
            elif x.Length <> y.Length then false
            else
                let mutable idx = 0
                let mutable equal = true
                while equal && idx < x.Length do
                    if x[idx] <> y[idx] then
                        equal <- false
                    idx <- idx + 1
                equal

        member _.GetHashCode(array: byte[]) =
            if isNull (box array) then 0
            else
                let mutable hash = 17
                for value in array do
                    hash <- (hash * 23) + int value
                hash }

let private writeCompressedUnsigned (writer: BinaryWriter) (value: int) =
    if value <= 0x7F then
        writer.Write(byte value)
    elif value <= 0x3FFF then
        let b1 = byte ((value >>> 8) ||| 0x80)
        let b0 = byte (value &&& 0xFF)
        writer.Write(b1)
        writer.Write(b0)
    elif value <= 0x1FFFFFFF then
        let b2 = byte ((value >>> 24) ||| 0xC0)
        let b1 = byte ((value >>> 16) &&& 0xFF)
        let b0 = byte ((value >>> 8) &&& 0xFF)
        let bLowest = byte (value &&& 0xFF)
        writer.Write(b2)
        writer.Write(b1)
        writer.Write(b0)
        writer.Write(bLowest)
    else
        invalidArg (nameof value) "Compressed integer is too large for CLI metadata."

type private RowTableBuilder() =
    let rows = ResizeArray<RowElementData[]>()

    member _.Add(elements: RowElementData[]) = rows.Add elements
    member _.Entries = rows.ToArray()
    member _.Count = rows.Count

type private StringHeapBuilder() =
    let entries = ResizeArray<string>()
    let lookup = Dictionary<string, int>(StringComparer.Ordinal)
    let utf8 = Encoding.UTF8
    let mutable bytesCache: byte[] option = None
    let mutable offsetsCache: int[] option = None

    member _.AddSharedEntry(value: string) : int =
        if String.IsNullOrEmpty value then
            0
        else
            match lookup.TryGetValue value with
            | true, index -> index
            | _ ->
                let index = entries.Count + 1
                entries.Add value
                lookup[value] <- index
                bytesCache <- None
                offsetsCache <- None
                index

    member private this.BuildIfNeeded() =
        match bytesCache, offsetsCache with
        | Some _, Some _ -> ()
        | _ ->
            use ms = new MemoryStream()
            use writer = new BinaryWriter(ms, utf8, leaveOpen = true)
            let entryOffsets = Array.zeroCreate (entries.Count + 1)
            writer.Write(byte 0)
            let mutable currentOffset = int ms.Length
            for i = 0 to entries.Count - 1 do
                let entryIndex = i + 1
                entryOffsets.[entryIndex] <- currentOffset
                let bytes = utf8.GetBytes entries.[i]
                writer.Write(bytes)
                writer.Write(byte 0)
                currentOffset <- currentOffset + bytes.Length + 1
            writer.Flush()
            bytesCache <- Some(ms.ToArray())
            offsetsCache <- Some entryOffsets

    member this.Bytes
        with get () =
            this.BuildIfNeeded()
            bytesCache.Value

    member this.EntryOffsets
        with get () =
            this.BuildIfNeeded()
            offsetsCache.Value

type private ByteArrayHeapBuilder() =
    let entries = ResizeArray<byte[]>()
    let lookup = Dictionary<byte[], int>(byteArrayComparer)
    let mutable bytesCache: byte[] option = None
    let mutable offsetsCache: int[] option = None

    let encodeCompressedUnsigned value =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        writeCompressedUnsigned writer value
        writer.Flush()
        ms.ToArray()

    member _.AddSharedEntry(value: byte[]) : int =
        if isNull (box value) || value.Length = 0 then
            0
        else
            match lookup.TryGetValue value with
            | true, index -> index
            | _ ->
                let index = entries.Count + 1
                entries.Add value
                lookup[value] <- index
                bytesCache <- None
                offsetsCache <- None
                index

    member private this.BuildIfNeeded() =
        match bytesCache, offsetsCache with
        | Some _, Some _ -> ()
        | _ ->
            use ms = new MemoryStream()
            use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
            let entryOffsets = Array.zeroCreate (entries.Count + 1)
            writer.Write(byte 0)
            let mutable currentOffset = int ms.Length
            for i = 0 to entries.Count - 1 do
                let entryIndex = i + 1
                entryOffsets.[entryIndex] <- currentOffset
                let value = entries.[i]
                writeCompressedUnsigned writer value.Length
                if value.Length > 0 then
                    writer.Write(value)
                currentOffset <- int ms.Length
            writer.Flush()
            bytesCache <- Some(ms.ToArray())
            offsetsCache <- Some entryOffsets

    member this.Bytes
        with get () =
            this.BuildIfNeeded()
            bytesCache.Value

    member this.EntryOffsets
        with get () =
            this.BuildIfNeeded()
            offsetsCache.Value

    member _.Entries = entries |> Seq.toArray

type private UserStringHeapBuilder() =
    let entries = HashSet<int>()
    let mutable buffer : byte[] option = None
    let mutable maxLength = 1
    let mutable bytesCache : byte[] option = None

    let encodeUserString (value: string) =
        let blobBuilder = BlobBuilder()
        blobBuilder.WriteUserString(value)
        blobBuilder.ToArray()

    let ensureBuffer lengthNeeded =
        let requiredLength = max lengthNeeded 1
        match buffer with
        | Some existing when existing.Length >= requiredLength -> existing
        | Some existing ->
            let resized = Array.zeroCreate<byte> requiredLength
            Buffer.BlockCopy(existing, 0, resized, 0, existing.Length)
            buffer <- Some resized
            resized
        | None ->
            let initial = Array.zeroCreate<byte> requiredLength
            initial[0] <- 0uy
            buffer <- Some initial
            initial

    member _.AddEntry(offset: int, value: string) =
        if offset <= 0 then
            ()
        elif entries.Add offset then
            let bytes = encodeUserString value
            let neededLength = offset + bytes.Length
            let storage = ensureBuffer neededLength
            Buffer.BlockCopy(bytes, 0, storage, offset, bytes.Length)
            maxLength <- max maxLength neededLength
            bytesCache <- None

    member this.Bytes
        with get () =
            match buffer with
            | Some data ->
                match bytesCache with
                | Some cached -> cached
                | None ->
                    let length = max maxLength 1
                    let trimmed =
                        if data.Length = length then
                            data
                        else
                            let slice = Array.zeroCreate<byte> length
                            Buffer.BlockCopy(data, 0, slice, 0, min data.Length length)
                            slice
                    bytesCache <- Some trimmed
                    trimmed
            | None ->
                let minimal = Array.zeroCreate<byte> 1
                minimal[0] <- 0uy
                minimal

type DeltaMetadataTables(?heapOffsets: MetadataHeapOffsets) =
    let heapOffsets = defaultArg heapOffsets MetadataHeapOffsets.Zero
    let strings = StringHeapBuilder()
    let blobs = ByteArrayHeapBuilder()
    let guids = ByteArrayHeapBuilder()
    let userStrings = UserStringHeapBuilder()
    let mutable stringHeapBytesCache: byte[] option = None
    let mutable blobHeapBytesCache: byte[] option = None
    let mutable guidHeapBytesCache: byte[] option = None
    let mutable userStringHeapBytesCache: byte[] option = None

    let moduleRows = RowTableBuilder()
    let methodRows = RowTableBuilder()
    let paramRows = RowTableBuilder()
    let typeRefRows = RowTableBuilder()
    let memberRefRows = RowTableBuilder()
    let assemblyRefRows = RowTableBuilder()
    let standAloneSigRows = RowTableBuilder()
    let customAttributeRows = RowTableBuilder()
    let propertyRows = RowTableBuilder()
    let eventRows = RowTableBuilder()
    let propertyMapRows = RowTableBuilder()
    let eventMapRows = RowTableBuilder()
    let methodSemanticsRows = RowTableBuilder()
    let encLogRows = RowTableBuilder()
    let encMapRows = RowTableBuilder()

    let rowElement tag value =
        { Tag = tag
          Value = value
          IsAbsolute = false }

    let rowElementAbsolute tag value =
        { Tag = tag
          Value = value
          IsAbsolute = true }

    let rowElementUShort (value: uint16) = rowElement RowElementTags.UShort (int value)
    let rowElementULong (value: int) = rowElement RowElementTags.ULong value
    let rowElementString value = rowElement RowElementTags.String value
    let rowElementBlob value = rowElement RowElementTags.Blob value
    let rowElementStringAbsolute value = rowElementAbsolute RowElementTags.String value
    let rowElementBlobAbsolute value = rowElementAbsolute RowElementTags.Blob value
    let rowElementGuid value = rowElement RowElementTags.Guid value
    let rowElementSimpleIndex table value = rowElement (RowElementTags.SimpleIndex table) value
    let rowElementTypeDefOrRef tag value = rowElement (RowElementTags.TypeDefOrRefOrSpec tag) value
    let rowElementHasSemantics tag value = rowElement (RowElementTags.HasSemantics tag) value
    let rowElementResolutionScope kind rowId =
        let tagValue =
            match kind with
            | HandleKind.ModuleDefinition -> 0
            | HandleKind.ModuleReference -> 1
            | HandleKind.AssemblyReference -> 2
            | HandleKind.TypeReference -> 3
            | _ -> invalidArg (nameof kind) "Unsupported resolution scope"
        rowElement (RowElementTags.ResolutionScopeMin + tagValue) rowId

    let rowElementMemberRefParent kind rowId =
        let tagValue =
            match kind with
            | HandleKind.TypeDefinition -> 0
            | HandleKind.TypeReference -> 1
            | HandleKind.ModuleReference -> 2
            | HandleKind.MethodDefinition -> 3
            | HandleKind.TypeSpecification -> 4
            | _ -> invalidArg (nameof kind) "Unsupported member ref parent"
        rowElement (RowElementTags.MemberRefParentMin + tagValue) rowId

    let rowElementHasCustomAttribute kind rowId =
        let tagValue =
            match kind with
            | HandleKind.MethodDefinition -> 0
            | _ -> invalidArg (nameof kind) "Unsupported custom attribute parent"
        rowElement (RowElementTags.HasCustomAttributeMin + tagValue) rowId

    let rowElementCustomAttributeType kind rowId =
        let tag =
            match kind with
            | HandleKind.MethodDefinition -> cat_MethodDef
            | HandleKind.MemberReference -> cat_MemberRef
            | _ -> invalidArg (nameof kind) "Unsupported custom attribute constructor"
        rowElement (RowElementTags.CustomAttributeType tag) rowId

    let addStringValue (value: string) = if String.IsNullOrEmpty value then 0 else strings.AddSharedEntry value

    let addExistingStringHandle (handleOpt: StringHandle option) (value: string) : int * bool =
        match handleOpt with
        | Some handle when not handle.IsNil -> MetadataTokens.GetHeapOffset handle, true
        | _ ->
            let idx = addStringValue value
            idx, false

    let addExistingStringOptionHandle (handleOpt: StringHandle option) (valueOpt: string option) : int * bool =
        match handleOpt with
        | Some handle when not handle.IsNil -> MetadataTokens.GetHeapOffset handle, true
        | _ ->
            match valueOpt with
            | Some v when not (String.IsNullOrEmpty v) -> strings.AddSharedEntry v, false
            | _ -> 0, false

    let addStringOption (value: string option) : int * bool =
        match value with
        | Some v when not (String.IsNullOrEmpty v) ->
            let idx = strings.AddSharedEntry v
            idx, false
        | _ -> 0, false

    let addBlobBytes (bytes: byte[]) = if obj.ReferenceEquals(bytes, null) || bytes.Length = 0 then 0 else blobs.AddSharedEntry bytes

    let addExistingBlobHandle (handleOpt: BlobHandle option) (value: byte[]) : int * bool =
        match handleOpt with
        | Some handle when not handle.IsNil -> MetadataTokens.GetHeapOffset handle, true
        | _ ->
            let idx = addBlobBytes value
            idx, false

    let addGuidValue (value: Guid) =
        if value = System.Guid.Empty then 0 else guids.AddSharedEntry(value.ToByteArray())

    let stringElement (token, isAbsolute) = if isAbsolute then rowElementStringAbsolute token else rowElementString token
    let blobElement (token, isAbsolute) = if isAbsolute then rowElementBlobAbsolute token else rowElementBlob token

    let encodeTypeDefOrRef (handle: EntityHandle) =
        if handle.IsNil then
            tdor_TypeDef, 0
        else
            let baseHandle = EntityHandle.op_Implicit handle
            match handle.Kind with
            | HandleKind.TypeDefinition -> tdor_TypeDef, MetadataTokens.GetRowNumber(TypeDefinitionHandle.op_Explicit baseHandle)
            | HandleKind.TypeReference -> tdor_TypeRef, MetadataTokens.GetRowNumber(TypeReferenceHandle.op_Explicit baseHandle)
            | HandleKind.TypeSpecification -> tdor_TypeSpec, MetadataTokens.GetRowNumber(TypeSpecificationHandle.op_Explicit baseHandle)
            | _ -> tdor_TypeDef, 0

    let buildStringHeapBytes () = strings.Bytes

    let buildBlobHeapBytes () = blobs.Bytes

    let buildGuidHeapBytes () =
        use ms = new MemoryStream()
        use writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen = true)
        // Guid heap index 0 refers to null; keep a single 0 GUID there.
        writer.Write(Array.zeroCreate<byte> 16)
        for entry in guids.Entries do
            if entry.Length = 16 then
                writer.Write(entry)
            else
                invalidArg "entry" "GUID entries must be 16 bytes."
        writer.Flush()
        ms.ToArray()
    let buildUserStringHeapBytes () = userStrings.Bytes

    member _.AddModuleRow(name: string, nameHandleOpt: StringHandle option, moduleId: Guid, encId: Guid, encBaseId: Guid) =
        if moduleRows.Count = 0 then
            let nameToken =
                match nameHandleOpt with
                | Some handle when not handle.IsNil -> MetadataTokens.GetHeapOffset handle, true
                | _ -> addStringValue name, false
            let row =
                [|
                    rowElementUShort 0us
                    stringElement nameToken
                    rowElementGuid (addGuidValue moduleId)
                    rowElementGuid (addGuidValue encId)
                    rowElementGuid (addGuidValue encBaseId)
                |]
            moduleRows.Add row

    member _.AddMethodRow(row: MethodDefinitionRowInfo, body: MethodBodyUpdate) =
        let nameToken = addExistingStringHandle row.NameHandle row.Name

        let signatureToken = addExistingBlobHandle row.SignatureHandle row.Signature

        let codeRva =
            if body.CodeLength > 0 then
                body.CodeOffset
            else
                match row.CodeRva with
                | Some rva -> rva
                | None -> 0

        let rowElements =
            [|
                rowElementULong codeRva
                rowElementUShort (uint16 row.ImplAttributes)
                rowElementUShort (uint16 row.Attributes)
                stringElement nameToken
                blobElement signatureToken
                rowElementSimpleIndex TableNames.Param (row.FirstParameterRowId |> Option.defaultValue 0)
            |]
        methodRows.Add rowElements

    member _.AddParameterRow(row: ParameterDefinitionRowInfo) =
        let nameToken = addExistingStringOptionHandle row.NameHandle row.Name
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementUShort (uint16 row.SequenceNumber)
                stringElement nameToken
            |]
        paramRows.Add rowElements

    member _.AddTypeReferenceRow(row: TypeReferenceRowInfo) =
        let struct (scopeKind, scopeRowId) = row.ResolutionScope
        let nameToken = addExistingStringHandle row.NameHandle row.Name
        let namespaceToken = addExistingStringHandle row.NamespaceHandle row.Namespace
        let rowElements =
            [|
                rowElementResolutionScope scopeKind scopeRowId
                stringElement nameToken
                stringElement namespaceToken
            |]
        typeRefRows.Add rowElements

    member _.AddMemberReferenceRow(row: MemberReferenceRowInfo) =
        let struct (parentKind, parentRowId) = row.Parent
        let nameToken = addExistingStringHandle row.NameHandle row.Name
        let signatureToken = addExistingBlobHandle row.SignatureHandle row.Signature
        let rowElements =
            [|
                rowElementMemberRefParent parentKind parentRowId
                stringElement nameToken
                blobElement signatureToken
            |]
        memberRefRows.Add rowElements

    member _.AddAssemblyReferenceRow(row: AssemblyReferenceRowInfo) =
        let publicKeyToken = addExistingBlobHandle row.PublicKeyOrTokenHandle row.PublicKeyOrToken
        let nameToken = addExistingStringHandle row.NameHandle row.Name
        let cultureToken = addExistingStringOptionHandle row.CultureHandle row.Culture
        let hashToken = addExistingBlobHandle row.HashValueHandle row.HashValue
        let versionComponent value =
            if value >= 0s then uint16 value else 0us
        let rowElements =
            [|
                rowElementUShort (versionComponent (int16 row.Version.Major))
                rowElementUShort (versionComponent (int16 row.Version.Minor))
                rowElementUShort (versionComponent (int16 row.Version.Build))
                rowElementUShort (versionComponent (int16 row.Version.Revision))
                rowElementULong (int row.Flags)
                blobElement publicKeyToken
                stringElement nameToken
                stringElement cultureToken
                blobElement hashToken
            |]
        assemblyRefRows.Add rowElements

    member _.AddStandaloneSignatureRow(signatureBytes: byte[]) =
        if not (isNull (box signatureBytes)) && signatureBytes.Length > 0 then
            let blobIndex = addBlobBytes signatureBytes
            let rowElements =
                [|
                    blobElement (blobIndex, false)
                |]
            standAloneSigRows.Add rowElements

    member _.AddCustomAttributeRow(row: CustomAttributeRowInfo) =
        let parentElement =
            let struct (kind, rowId) = row.Parent
            rowElementHasCustomAttribute kind rowId

        let ctorElement =
            let struct (kind, rowId) = row.Constructor
            rowElementCustomAttributeType kind rowId

        let valueToken = addExistingBlobHandle row.ValueHandle row.Value

        let rowElements =
            [|
                parentElement
                ctorElement
                blobElement valueToken
            |]

        customAttributeRows.Add rowElements

    member _.AddPropertyRow(row: PropertyDefinitionRowInfo) =
        let nameToken = addExistingStringHandle row.NameHandle row.Name

        let signatureToken = addExistingBlobHandle row.SignatureHandle row.Signature

        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                stringElement nameToken
                blobElement signatureToken
            |]
        propertyRows.Add rowElements

    member _.AddEventRow(row: EventDefinitionRowInfo) =
        let tdorTag, tdorRow = encodeTypeDefOrRef row.EventType
        let nameToken = addExistingStringHandle row.NameHandle row.Name
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                stringElement nameToken
                rowElementTypeDefOrRef tdorTag tdorRow
            |]
        eventRows.Add rowElements

    member _.AddPropertyMapRow(row: PropertyMapRowInfo) =
        let rowElements =
            [|
                rowElementSimpleIndex TableNames.TypeDef row.TypeDefRowId
                rowElementSimpleIndex TableNames.Property (row.FirstPropertyRowId |> Option.defaultValue 0)
            |]
        propertyMapRows.Add rowElements

    member _.AddEventMapRow(row: EventMapRowInfo) =
        let rowElements =
            [|
                rowElementSimpleIndex TableNames.TypeDef row.TypeDefRowId
                rowElementSimpleIndex TableNames.Event (row.FirstEventRowId |> Option.defaultValue 0)
            |]
        eventMapRows.Add rowElements

    member _.AddMethodSemanticsRow(row: MethodSemanticsMetadataUpdate) =
        let methodHandle = MetadataTokens.MethodDefinitionHandle row.MethodToken
        let methodRowId = MetadataTokens.GetRowNumber methodHandle
        let assocTag, assocRowId =
            match row.AssociationInfo with
            | Some(MethodSemanticsAssociation.PropertyAssociation(_, propertyRowId)) -> hs_Property, propertyRowId
            | Some(MethodSemanticsAssociation.EventAssociation(_, eventRowId)) -> hs_Event, eventRowId
            | None ->
                match row.Association.Kind with
                | HandleKind.PropertyDefinition ->
                    let assocHandle = PropertyDefinitionHandle.op_Explicit(EntityHandle.op_Implicit row.Association)
                    hs_Property, MetadataTokens.GetRowNumber assocHandle
                | HandleKind.EventDefinition ->
                    let assocHandle = EventDefinitionHandle.op_Explicit(EntityHandle.op_Implicit row.Association)
                    hs_Event, MetadataTokens.GetRowNumber assocHandle
                | _ -> hs_Property, 0
        let rowElements =
            [|
                rowElementUShort (uint16 row.Attributes)
                rowElementSimpleIndex TableNames.Method methodRowId
                rowElementHasSemantics assocTag assocRowId
            |]
        methodSemanticsRows.Add rowElements

    member _.AddEncLogRow(tableIndex: TableIndex, rowId: int, operation: EditAndContinueOperation) =
        let entityHandle = MetadataTokens.EntityHandle(tableIndex, rowId)
        let token = MetadataTokens.GetToken(entityHandle)
        let rowElements =
            [|
                rowElementULong token
                rowElementULong (int operation)
            |]
        encLogRows.Add rowElements

    member _.AddEncMapRow(tableIndex: TableIndex, rowId: int) =
        let entityHandle = MetadataTokens.EntityHandle(tableIndex, rowId)
        let token = MetadataTokens.GetToken(entityHandle)
        let rowElements =
            [|
                rowElementULong token
            |]
        encMapRows.Add rowElements

    member _.StringHeapBytes
        with get () =
            match stringHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildStringHeapBytes ()
                stringHeapBytesCache <- Some bytes
                bytes

    member _.StringHeapOffsets = strings.EntryOffsets

    member _.BlobHeapBytes
        with get () =
            match blobHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildBlobHeapBytes ()
                blobHeapBytesCache <- Some bytes
                bytes

    member _.BlobHeapOffsets = blobs.EntryOffsets

    member _.GuidHeapBytes
        with get () =
            match guidHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildGuidHeapBytes ()
                guidHeapBytesCache <- Some bytes
                bytes

    member _.UserStringHeapBytes
        with get () =
            match userStringHeapBytesCache with
            | Some bytes -> bytes
            | None ->
                let bytes = buildUserStringHeapBytes ()
                userStringHeapBytesCache <- Some bytes
                bytes

    member this.StringHeapSize = this.StringHeapBytes.Length

    member this.BlobHeapSize = this.BlobHeapBytes.Length

    member this.GuidHeapSize = this.GuidHeapBytes.Length

    member this.HeapSizes : MetadataHeapSizes =
        { StringHeapSize = this.StringHeapSize
          UserStringHeapSize = this.UserStringHeapBytes.Length
          BlobHeapSize = this.BlobHeapSize
          GuidHeapSize = this.GuidHeapSize }

    member _.TableRows : TableRows =
        { Module = moduleRows.Entries
          MethodDef = methodRows.Entries
          Param = paramRows.Entries
          TypeRef = typeRefRows.Entries
          MemberRef = memberRefRows.Entries
          AssemblyRef = assemblyRefRows.Entries
          StandAloneSig = standAloneSigRows.Entries
          CustomAttribute = customAttributeRows.Entries
          Property = propertyRows.Entries
          Event = eventRows.Entries
          PropertyMap = propertyMapRows.Entries
          EventMap = eventMapRows.Entries
          MethodSemantics = methodSemanticsRows.Entries
          EncLog = encLogRows.Entries
          EncMap = encMapRows.Entries }

    member _.HeapOffsets = heapOffsets

    member _.TableRowCounts : int[] =
        let counts = Array.zeroCreate MetadataTokens.TableCount
        counts[int TableIndex.Module] <- moduleRows.Count
        counts[int TableIndex.MethodDef] <- methodRows.Count
        counts[int TableIndex.Param] <- paramRows.Count
        counts[int TableIndex.TypeRef] <- typeRefRows.Count
        counts[int TableIndex.MemberRef] <- memberRefRows.Count
        counts[int TableIndex.AssemblyRef] <- assemblyRefRows.Count
        counts[int TableIndex.StandAloneSig] <- standAloneSigRows.Count
        counts[int TableIndex.CustomAttribute] <- customAttributeRows.Count
        counts[int TableIndex.Property] <- propertyRows.Count
        counts[int TableIndex.Event] <- eventRows.Count
        counts[int TableIndex.PropertyMap] <- propertyMapRows.Count
        counts[int TableIndex.EventMap] <- eventMapRows.Count
        counts[int TableIndex.MethodSemantics] <- methodSemanticsRows.Count
        counts[int TableIndex.EncLog] <- encLogRows.Count
        counts[int TableIndex.EncMap] <- encMapRows.Count
        counts

    member _.AddUserStringLiteral(offset: int, value: string) =
        userStrings.AddEntry(offset, value)
        userStringHeapBytesCache <- None
