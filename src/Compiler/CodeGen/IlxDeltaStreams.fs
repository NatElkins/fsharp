module internal FSharp.Compiler.IlxDeltaStreams

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

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
        Metadata: byte[]
        IL: byte[]
        MethodBodies: MethodBodyUpdate list
        StandaloneSignatures: StandaloneSignatureUpdate list
        EncLogEntries: (TableIndex * int * EditAndContinueOperation) list
        EncMapEntries: (TableIndex * int) list
    }

/// <summary>
/// Accumulates metadata tables, Edit-and-Continue bookkeeping, and encoded method bodies prior to serialising
/// a hot reload delta. The builder owns private instances of <see cref="MetadataBuilder"/> and <see cref="BlobBuilder"/>;
/// callers retrieve the resulting byte arrays via <see cref="Build"/>.
/// </summary>
type IlDeltaStreamBuilder() =
    let metadataBuilder = MetadataBuilder()
    let methodBodyStream = BlobBuilder()
    let methodBodies = ResizeArray<MethodBodyUpdate>()
    let standaloneSigs = ResizeArray<StandaloneSignatureUpdate>()
    let encLogEntries = ResizeArray<TableIndex * int * EditAndContinueOperation>()
    let encMapEntries = ResizeArray<TableIndex * int>()
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
    member _.AddMethodBody(methodToken: int, localSignatureToken: int, code: byte[]) =
        alignMethodStream ()
        let offset = methodBodyStream.Count
        methodBodyStream.WriteBytes(code)
        // Ensure the next method starts on the required alignment boundary.
        alignMethodStream ()

        let update =
            {
                MethodToken = methodToken
                LocalSignatureToken = localSignatureToken
                CodeOffset = offset
                CodeLength = code.Length
            }

        methodBodies.Add(update)
        update

    /// <summary>Adds a standalone signature blob to the metadata stream and returns its token.</summary>
    member _.AddStandaloneSignature(signature: byte[]) =
        if signature.Length = 0 then
            0
        else
            let blobHandle = metadataBuilder.GetOrAddBlob(signature)
            let handle = metadataBuilder.AddStandaloneSignature(blobHandle)
            let token = MetadataTokens.GetToken(EntityHandle.op_Implicit handle)
            standaloneSigs.Add({ Handle = handle; Blob = Array.copy signature })
            token

    /// <summary>Register an Edit-and-Continue log entry.</summary>
    member _.AddEncLogEntry(tableIndex: TableIndex, rowId: int, operation: EditAndContinueOperation) =
        let handle = MetadataTokens.EntityHandle(tableIndex, rowId)
        metadataBuilder.AddEncLogEntry(handle, operation) |> ignore
        encLogEntries.Add(tableIndex, rowId, operation)

    /// <summary>Register an Edit-and-Continue map entry.</summary>
    member _.AddEncMapEntry(tableIndex: TableIndex, rowId: int) =
        let handle = MetadataTokens.EntityHandle(tableIndex, rowId)
        metadataBuilder.AddEncMapEntry(handle) |> ignore
        encMapEntries.Add(tableIndex, rowId)

    /// <summary>
    /// Finalise the builder and emit the metadata and IL blobs. The builder can only be consumed once; subsequent
    /// invocations throw to prevent mismatched Edit-and-Continue state.
    /// </summary>
    member _.Build(moduleName: string, mvid: Guid, encId: Guid, encBaseId: Guid option) =
        if isBuilt then invalidOp "IlDeltaStreamBuilder.Build may only be called once per builder instance."
        isBuilt <- true

        let moduleNameHandle = metadataBuilder.GetOrAddString(moduleName)
        let mvidHandle = metadataBuilder.GetOrAddGuid(mvid)
        let encIdHandle = metadataBuilder.GetOrAddGuid(encId)
        let encBaseHandle =
            encBaseId
            |> Option.defaultValue Guid.Empty
            |> metadataBuilder.GetOrAddGuid

        // Generation 0 is a placeholder; callers will populate the actual generation number when integrating with the runtime.
        metadataBuilder.AddModule(0, moduleNameHandle, mvidHandle, encIdHandle, encBaseHandle) |> ignore

        let metadataBlob = BlobBuilder()
        let metadataRoot = new MetadataRootBuilder(metadataBuilder)
        metadataRoot.Serialize(metadataBlob, 0, 0)

        {
            Metadata = metadataBlob.ToArray()
            IL = methodBodyStream.ToArray()
            MethodBodies = methodBodies |> Seq.toList
            StandaloneSignatures = standaloneSigs |> Seq.toList
            EncLogEntries = encLogEntries |> Seq.toList
            EncMapEntries = encMapEntries |> Seq.toList
        }
