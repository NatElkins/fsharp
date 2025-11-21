module internal FSharp.Compiler.HotReloadPdb

open System
open System.Collections.Immutable
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Security.Cryptography
open FSharp.Compiler.HotReloadBaseline

let private computeRowCounts (reader: MetadataReader) : ImmutableArray<int> =
    let counts = Array.zeroCreate<int> MetadataTokens.TableCount

    let inline setCount (index: TableIndex) (value: int) =
        counts[int index] <- value

    setCount TableIndex.Document reader.Documents.Count
    setCount TableIndex.MethodDebugInformation reader.MethodDebugInformation.Count
    setCount TableIndex.LocalScope reader.LocalScopes.Count
    setCount TableIndex.LocalVariable reader.LocalVariables.Count
    setCount TableIndex.LocalConstant reader.LocalConstants.Count
    setCount TableIndex.ImportScope reader.ImportScopes.Count
    setCount TableIndex.CustomDebugInformation reader.CustomDebugInformation.Count

    ImmutableArray.CreateRange counts

let createSnapshot (pdbBytes: byte[]) : PortablePdbSnapshot =
    use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange pdbBytes)
    let reader = provider.GetMetadataReader()
    let rowCounts = computeRowCounts reader
    let entryPointHandle = reader.DebugMetadataHeader.EntryPoint

    let entryPointToken =
        if entryPointHandle.IsNil then
            None
        else
            let entityHandle: EntityHandle = MethodDefinitionHandle.op_Implicit entryPointHandle
            Some(MetadataTokens.GetToken entityHandle)

    { Bytes = Array.copy pdbBytes
      TableRowCounts = rowCounts
      EntryPointToken = entryPointToken }

let emitDelta
    (baseline: FSharpEmitBaseline)
    (updatedPdbBytes: byte[])
    (addedOrChangedMethods: AddedOrChangedMethodInfo list)
    (deltaToUpdatedMethodToken: IReadOnlyDictionary<int, int>)
    (metadataEncLog: (TableIndex * int * EditAndContinueOperation) array)
    (metadataEncMap: (TableIndex * int) array)
    : byte[] option =
    match baseline.PortablePdb with
    | None -> None
    | Some snapshot ->
        let distinctTokens =
            addedOrChangedMethods
            |> List.map (fun info -> info.MethodToken)
            |> List.distinct
            |> List.filter (fun token -> token <> 0)

        if List.isEmpty distinctTokens then
            printfn "[hotreload-pdb] distinct token list empty"
            None
        else
            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.CreateRange updatedPdbBytes)
            let reader = provider.GetMetadataReader()
            let metadata = MetadataBuilder()
            let documentMap = Dictionary<DocumentHandle, DocumentHandle>()
            let mutable emitted = false

            let getOrAddDocument (sourceHandle: DocumentHandle) =
                match documentMap.TryGetValue sourceHandle with
                | true, handle -> handle
                | _ ->
                    let document = reader.GetDocument sourceHandle
                    let nameBytes = reader.GetBlobBytes document.Name
                    let hashBytes =
                        if document.Hash.IsNil then
                            Array.empty<byte>
                        else
                            reader.GetBlobBytes document.Hash

                    let hashAlgorithmGuid =
                        if document.HashAlgorithm.IsNil then
                            Guid.Empty
                        else
                            reader.GetGuid document.HashAlgorithm

                    let languageGuid =
                        if document.Language.IsNil then
                            Guid.Empty
                        else
                            reader.GetGuid document.Language

                    let nameHandle = metadata.GetOrAddBlob nameBytes
                    let hashHandle = metadata.GetOrAddBlob hashBytes
                    let hashAlgorithmHandle = metadata.GetOrAddGuid hashAlgorithmGuid
                    let languageHandle = metadata.GetOrAddGuid languageGuid

                    let added =
                        metadata.AddDocument(nameHandle, hashAlgorithmHandle, hashHandle, languageHandle)

                    documentMap[sourceHandle] <- added
                    added

            for token in distinctTokens do
                let sourceToken =
                    match deltaToUpdatedMethodToken.TryGetValue token with
                    | true, mapped -> mapped
                    | _ -> token

                if sourceToken = 0 then
                    printfn "[hotreload-pdb] method token missing for delta token 0x%08x" token
                else
                    let sourceHandle = MetadataTokens.MethodDefinitionHandle sourceToken

                    if sourceHandle.IsNil then
                        printfn "[hotreload-pdb] source handle nil for delta token 0x%08x (source token=0x%08x)" token sourceToken
                    else
                        let methodRow = MetadataTokens.GetRowNumber sourceHandle

                        if methodRow <= reader.MethodDebugInformation.Count then
                            let methodInfo = reader.GetMethodDebugInformation sourceHandle
                            let targetDocument =
                                if methodInfo.Document.IsNil then
                                    DocumentHandle()
                                else
                                    getOrAddDocument methodInfo.Document

                            let sequencePointsHandle =
                                if methodInfo.SequencePointsBlob.IsNil then
                                    BlobHandle()
                                else
                                    metadata.GetOrAddBlob(reader.GetBlobBytes methodInfo.SequencePointsBlob)

                            metadata.AddMethodDebugInformation(targetDocument, sequencePointsHandle) |> ignore
                            emitted <- true
                        else
                            printfn
                                "[hotreload-pdb] missing method debug row %d (delta token=0x%08x, source token=0x%08x, count=%d)"
                                methodRow
                                token
                                sourceToken
                                reader.MethodDebugInformation.Count

            // Mirror metadata EncLog/EncMap so PDB delta stays in lockstep with metadata delta tables.
            for (table, rowId, operation) in metadataEncLog do
                let handle = MetadataTokens.EntityHandle(table, rowId)
                metadata.AddEncLogEntry(handle, operation)

            for (table, rowId) in metadataEncMap do
                let handle = MetadataTokens.EntityHandle(table, rowId)
                metadata.AddEncMapEntry(handle)

            if not emitted && (metadataEncLog.Length > 0 || metadataEncMap.Length > 0) then
                emitted <- true

            if not emitted then
                printfn "[hotreload-pdb] no method debug info emitted for tokens %A" distinctTokens
                None
            else
                let entryPointHandle =
                    match snapshot.EntryPointToken with
                    | Some token -> MetadataTokens.MethodDefinitionHandle token
                    | None -> MethodDefinitionHandle()

                let idProvider =
                    Func<IEnumerable<Blob>, BlobContentId>(fun content ->
                        use hasher = SHA256.Create()
                        let bytes =
                            content
                            |> Seq.collect (fun blob -> blob.GetBytes())
                            |> Array.ofSeq

                        BlobContentId.FromHash(hasher.ComputeHash bytes))

                let zeroCounts =
                    ImmutableArray.CreateRange(Array.zeroCreate<int> MetadataTokens.TableCount)

                let builder = PortablePdbBuilder(metadata, zeroCounts, entryPointHandle, idProvider)
                let blobBuilder = BlobBuilder()
                builder.Serialize blobBuilder |> ignore
                Some(blobBuilder.ToArray())
