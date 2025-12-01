module internal FSharp.Compiler.IlxDeltaStreams

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILDeltaHandles
open FSharp.Compiler.IO

/// <summary>Represents a method body update captured for an Edit-and-Continue delta.</summary>
type MethodBodyUpdate =
    {
        MethodToken: int
        LocalSignatureToken: int
        CodeOffset: int
        CodeLength: int
    }

/// <summary>Represents a standalone signature (e.g., local signature) emitted in the delta metadata.</summary>
type StandaloneSignatureUpdate =
    {
        RowId: int
        Blob: byte[]
    }

/// <summary>The emitted metadata and IL payloads produced by <see cref="IlDeltaStreamBuilder"/>.</summary>
type IlDeltaStreams =
    {
        IL: byte[]
        MethodBodies: MethodBodyUpdate list
        StandaloneSignatures: StandaloneSignatureUpdate list
    }

/// <summary>
/// Accumulates metadata tables, Edit-and-Continue bookkeeping, and encoded method bodies prior to serialising
/// a hot reload delta. The builder owns private instances of <see cref="MetadataBuilder"/> and <see cref="ByteBuffer"/>;
/// callers retrieve the resulting byte arrays via <see cref="Build"/>.
/// </summary>
type IlDeltaStreamBuilder(baselineMetadata: MetadataSnapshot option) =
    let metadataBuilder =
        match baselineMetadata with
        | Some snapshot ->
            let heaps = snapshot.HeapSizes
            let alignedGuidStart =
                let offset = snapshot.GuidHeapStart
                if offset % 16 = 0 then
                    offset
                else
                    ((offset + 15) / 16) * 16
            MetadataBuilder(
                userStringHeapStartOffset = heaps.UserStringHeapSize,
                stringHeapStartOffset = heaps.StringHeapSize,
                blobHeapStartOffset = heaps.BlobHeapSize,
                guidHeapStartOffset = alignedGuidStart
            )
        | None -> MetadataBuilder()
    let methodBodyStream = ByteBuffer.Create(256)
    let methodBodies = ResizeArray<MethodBodyUpdate>()
    let standaloneSigs = ResizeArray<StandaloneSignatureUpdate>()
    let standaloneSigCache = Dictionary<int, StandaloneSignatureHandle>()
    let mutable isBuilt = false

    let alignStream alignment =
        // Align to N-byte boundary by padding with zeros
        let pos = methodBodyStream.Position
        let padding = (alignment - (pos % alignment)) % alignment
        for _ = 1 to padding do
            methodBodyStream.EmitByte 0uy

    /// <summary>Expose the underlying metadata builder for advanced scenarios.</summary>
    member _.MetadataBuilder = metadataBuilder

    /// <summary>Inspection hook primarily used in unit tests.</summary>
    member _.MethodBodies = methodBodies |> Seq.toList

    member _.StandaloneSignatures = standaloneSigs |> Seq.toList

    /// <summary>Add a method body update for the supplied metadata token.</summary>
    member _.AddMethodBody(
        methodToken: int,
        localSignatureToken: int,
        ilBytes: byte[],
        maxStack: int,
        initLocals: bool,
        exceptionRegions: IlExceptionRegion[],
        remapEntityToken: int -> int
    ) =
        let ilLength = ilBytes.Length
        let hasExceptionRegions = exceptionRegions.Length > 0

        let flags =
            int e_CorILMethod_FatFormat
            ||| (if hasExceptionRegions then int e_CorILMethod_MoreSects else 0)
            ||| (if initLocals then int e_CorILMethod_InitLocals else 0)

        alignStream 4
        let offset = methodBodyStream.Position

        methodBodyStream.EmitByte(byte flags)
        methodBodyStream.EmitByte(0x30uy)
        methodBodyStream.EmitUInt16(uint16 maxStack)
        methodBodyStream.EmitInt32(ilLength)
        methodBodyStream.EmitInt32(localSignatureToken)
        methodBodyStream.EmitBytes(ilBytes)

        let padding = (4 - (ilLength % 4)) &&& 0x3
        if padding > 0 then
            for _ = 1 to padding do
                methodBodyStream.EmitByte 0uy

        if hasExceptionRegions then
            alignStream 4
            let regions = exceptionRegions
            let smallSize = regions.Length * 12 + 4
            let canUseSmall =
                smallSize <= 0xFF
                && regions
                   |> Array.forall (fun region ->
                       region.TryOffset <= 0xFFFF
                       && region.HandlerOffset <= 0xFFFF
                       && region.TryLength <= 0xFF
                       && region.HandlerLength <= 0xFF)

            let encodeKind (region: IlExceptionRegion) : int * int =
                match region.Kind with
                | IlExceptionRegionKind.Catch ->
                    let token =
                        if region.CatchTypeToken = 0 then 0
                        else remapEntityToken region.CatchTypeToken
                    e_COR_ILEXCEPTION_CLAUSE_EXCEPTION, token
                | IlExceptionRegionKind.Filter -> e_COR_ILEXCEPTION_CLAUSE_FILTER, region.FilterOffset
                | IlExceptionRegionKind.Finally -> e_COR_ILEXCEPTION_CLAUSE_FINALLY, 0
                | IlExceptionRegionKind.Fault -> e_COR_ILEXCEPTION_CLAUSE_FAULT, 0
                | _ -> e_COR_ILEXCEPTION_CLAUSE_EXCEPTION, 0

            if canUseSmall then
                methodBodyStream.EmitByte(e_CorILMethod_Sect_EHTable)
                methodBodyStream.EmitByte(byte smallSize)
                methodBodyStream.EmitByte(0uy)
                methodBodyStream.EmitByte(0uy)
                for region in regions do
                    let kind, extra = encodeKind region
                    methodBodyStream.EmitUInt16(uint16 kind)
                    methodBodyStream.EmitUInt16(uint16 region.TryOffset)
                    methodBodyStream.EmitByte(byte region.TryLength)
                    methodBodyStream.EmitUInt16(uint16 region.HandlerOffset)
                    methodBodyStream.EmitByte(byte region.HandlerLength)
                    methodBodyStream.EmitInt32(extra)
            else
                let bigSize = regions.Length * 24 + 4
                methodBodyStream.EmitByte(e_CorILMethod_Sect_EHTable ||| e_CorILMethod_Sect_FatFormat)
                methodBodyStream.EmitByte(byte bigSize)
                methodBodyStream.EmitByte(byte (bigSize >>> 8))
                methodBodyStream.EmitByte(byte (bigSize >>> 16))
                for region in regions do
                    let kind, extra = encodeKind region
                    methodBodyStream.EmitInt32(kind)
                    methodBodyStream.EmitInt32(region.TryOffset)
                    methodBodyStream.EmitInt32(region.TryLength)
                    methodBodyStream.EmitInt32(region.HandlerOffset)
                    methodBodyStream.EmitInt32(region.HandlerLength)
                    methodBodyStream.EmitInt32(extra)

        let update =
            {
                MethodToken = methodToken
                LocalSignatureToken = localSignatureToken
                CodeOffset = offset
                CodeLength = ilLength
            }

        methodBodies.Add(update)
        update

    /// <summary>Adds a standalone signature blob to the metadata stream and returns its token.</summary>
    member _.AddStandaloneSignature(signature: byte[]) =
        if signature.Length = 0 then
            0
        else
            let blobHandle = metadataBuilder.GetOrAddBlob(signature)
            let blobOffset = MetadataTokens.GetHeapOffset blobHandle
            match standaloneSigCache.TryGetValue blobOffset with
            | true, existing ->
                let entityHandle: EntityHandle = existing
                MetadataTokens.GetToken(entityHandle)
            | _ ->
                let handle = metadataBuilder.AddStandaloneSignature(blobHandle)
                standaloneSigCache[blobOffset] <- handle
                let entityHandle: EntityHandle = handle
                let token = MetadataTokens.GetToken(entityHandle)
                let rowId = MetadataTokens.GetRowNumber(entityHandle)
                standaloneSigs.Add({ RowId = rowId; Blob = Array.copy signature })
                token

    /// <summary>
    /// Finalise the builder and emit the metadata and IL blobs. The builder can only be consumed once; subsequent
    /// invocations throw to prevent mismatched Edit-and-Continue state.
    /// </summary>
    member _.Build() =
        if isBuilt then invalidOp "IlDeltaStreamBuilder.Build may only be called once per builder instance."
        isBuilt <- true

        {
            IL = methodBodyStream.AsMemory().ToArray()
            MethodBodies = methodBodies |> Seq.toList
            StandaloneSignatures = standaloneSigs |> Seq.toList
        }
