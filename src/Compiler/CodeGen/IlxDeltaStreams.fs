module internal FSharp.Compiler.IlxDeltaStreams

open System
open System.IO
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.BinaryConstants
open FSharp.Compiler.AbstractIL.ILBinaryWriter

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
        Handle: StandaloneSignatureHandle
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
/// a hot reload delta. The builder owns private instances of <see cref="MetadataBuilder"/> and <see cref="BlobBuilder"/>;
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
    let methodBodyStream = BlobBuilder()
    let methodBodies = ResizeArray<MethodBodyUpdate>()
    let standaloneSigs = ResizeArray<StandaloneSignatureUpdate>()
    let standaloneSigCache = Dictionary<int, StandaloneSignatureHandle>()
    let mutable isBuilt = false

    let alignMethodStream () =
        // ECMA-335 II.25.4.5 requires method bodies to start at 4-byte aligned addresses.
        methodBodyStream.Align(4)

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
        exceptionRegions: ImmutableArray<ExceptionRegion>,
        remapEntityToken: int -> int
    ) =
        let ilLength = ilBytes.Length
        let hasExceptionRegions = not exceptionRegions.IsDefaultOrEmpty

        let flags =
            int e_CorILMethod_FatFormat
            ||| (if hasExceptionRegions then int e_CorILMethod_MoreSects else 0)
            ||| (if initLocals then int e_CorILMethod_InitLocals else 0)

        alignMethodStream ()
        let offset = methodBodyStream.Count

        methodBodyStream.WriteByte(byte flags)
        methodBodyStream.WriteByte(0x30uy)
        methodBodyStream.WriteUInt16(uint16 maxStack)
        methodBodyStream.WriteInt32(ilLength)
        methodBodyStream.WriteInt32(localSignatureToken)
        methodBodyStream.WriteBytes(ilBytes)

        let padding = (4 - (ilLength % 4)) &&& 0x3
        if padding > 0 then
            let padBytes: byte[] = Array.zeroCreate padding
            methodBodyStream.WriteBytes(padBytes)

        if hasExceptionRegions then
            methodBodyStream.Align(4)
            let regions = exceptionRegions |> Seq.toList
            let smallSize = regions.Length * 12 + 4
            let canUseSmall =
                smallSize <= 0xFF
                && regions
                   |> List.forall (fun region ->
                       region.TryOffset <= 0xFFFF
                       && region.HandlerOffset <= 0xFFFF
                       && region.TryLength <= 0xFF
                       && region.HandlerLength <= 0xFF)

            let encodeKind (region: ExceptionRegion) : int * int =
                match region.Kind with
                | ExceptionRegionKind.Catch ->
                    let token =
                        if region.CatchType.IsNil then
                            0
                        else
                            let original = MetadataTokens.GetToken(region.CatchType)
                            remapEntityToken original
                    e_COR_ILEXCEPTION_CLAUSE_EXCEPTION, token
                | ExceptionRegionKind.Filter -> e_COR_ILEXCEPTION_CLAUSE_FILTER, region.FilterOffset
                | ExceptionRegionKind.Finally -> e_COR_ILEXCEPTION_CLAUSE_FINALLY, 0
                | ExceptionRegionKind.Fault -> e_COR_ILEXCEPTION_CLAUSE_FAULT, 0
                | _ -> e_COR_ILEXCEPTION_CLAUSE_EXCEPTION, 0

            if canUseSmall then
                methodBodyStream.WriteByte(e_CorILMethod_Sect_EHTable)
                methodBodyStream.WriteByte(byte smallSize)
                methodBodyStream.WriteByte(0uy)
                methodBodyStream.WriteByte(0uy)
                for region in regions do
                    let kind, extra = encodeKind region
                    methodBodyStream.WriteUInt16(uint16 kind)
                    methodBodyStream.WriteUInt16(uint16 region.TryOffset)
                    methodBodyStream.WriteByte(byte region.TryLength)
                    methodBodyStream.WriteUInt16(uint16 region.HandlerOffset)
                    methodBodyStream.WriteByte(byte region.HandlerLength)
                    methodBodyStream.WriteInt32(extra)
            else
                let bigSize = regions.Length * 24 + 4
                methodBodyStream.WriteByte(e_CorILMethod_Sect_EHTable ||| e_CorILMethod_Sect_FatFormat)
                methodBodyStream.WriteByte(byte bigSize)
                methodBodyStream.WriteByte(byte (bigSize >>> 8))
                methodBodyStream.WriteByte(byte (bigSize >>> 16))
                for region in regions do
                    let kind, extra = encodeKind region
                    methodBodyStream.WriteInt32(kind)
                    methodBodyStream.WriteInt32(region.TryOffset)
                    methodBodyStream.WriteInt32(region.TryLength)
                    methodBodyStream.WriteInt32(region.HandlerOffset)
                    methodBodyStream.WriteInt32(region.HandlerLength)
                    methodBodyStream.WriteInt32(extra)

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
            | true, existing -> MetadataTokens.GetToken(EntityHandle.op_Implicit existing)
            | _ ->
                let handle = metadataBuilder.AddStandaloneSignature(blobHandle)
                standaloneSigCache[blobOffset] <- handle
                let token = MetadataTokens.GetToken(EntityHandle.op_Implicit handle)
                standaloneSigs.Add({ Handle = handle; Blob = Array.copy signature })
                token

    /// <summary>
    /// Finalise the builder and emit the metadata and IL blobs. The builder can only be consumed once; subsequent
    /// invocations throw to prevent mismatched Edit-and-Continue state.
    /// </summary>
    member _.Build() =
        if isBuilt then invalidOp "IlDeltaStreamBuilder.Build may only be called once per builder instance."
        isBuilt <- true

        {
            IL = methodBodyStream.ToArray()
            MethodBodies = methodBodies |> Seq.toList
            StandaloneSignatures = standaloneSigs |> Seq.toList
        }
