module internal FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.IlxDeltaStreams

type MethodMetadataUpdate =
    {
        MethodToken: int
        MethodHandle: MethodDefinitionHandle
        Body: MethodBodyUpdate
    }

type MetadataDelta =
    {
        Metadata: byte[]
        EncLog: (TableIndex * int * EditAndContinueOperation) array
        EncMap: (TableIndex * int) array
    }

let emit (metadataReader: MetadataReader) (encId: Guid) (encBaseId: Guid) (updates: MethodMetadataUpdate list) : MetadataDelta =
    if List.isEmpty updates then
        { Metadata = Array.empty
          EncLog = Array.empty
          EncMap = Array.empty }
    else
        let metadataBuilder = MetadataBuilder()

        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleName = metadataReader.GetString moduleDef.Name
        let moduleNameHandle = metadataBuilder.GetOrAddString(moduleName)
        let mvid = metadataReader.GetGuid(moduleDef.Mvid)
        let mvidHandle = metadataBuilder.GetOrAddGuid(mvid)
        let encIdHandle = metadataBuilder.GetOrAddGuid(encId)
        let encBaseHandle = metadataBuilder.GetOrAddGuid(encBaseId)
        metadataBuilder.AddModule(0, moduleNameHandle, mvidHandle, encIdHandle, encBaseHandle) |> ignore

        // Sort method updates by baseline row id to produce deterministic ordering.
        let orderedUpdates =
            updates
            |> List.sortBy (fun u -> MetadataTokens.GetRowNumber(u.MethodHandle))

        let mutable encLog = ResizeArray()
        let mutable encMap = ResizeArray()

        for update in orderedUpdates do
            let methodDef = metadataReader.GetMethodDefinition update.MethodHandle

            let methodName = metadataReader.GetString methodDef.Name
            let nameHandle = metadataBuilder.GetOrAddString methodName

            let signatureBytes = metadataReader.GetBlobBytes methodDef.Signature
            let signatureHandle = metadataBuilder.GetOrAddBlob signatureBytes

            let firstParamHandle =
                let mutable enumerator = methodDef.GetParameters().GetEnumerator()
                if enumerator.MoveNext() then
                    MetadataTokens.ParameterHandle(MetadataTokens.GetRowNumber(enumerator.Current))
                else
                    ParameterHandle()

            metadataBuilder.AddMethodDefinition(
                methodDef.Attributes,
                methodDef.ImplAttributes,
                nameHandle,
                signatureHandle,
                update.Body.CodeOffset,
                firstParamHandle
            ) |> ignore

            let rowId = update.MethodToken &&& 0x00FFFFFF
            encLog.Add(struct (TableIndex.MethodDef, rowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.MethodDef, rowId))

        let metadataRoot = new MetadataRootBuilder(metadataBuilder)
        let metadataBlob = BlobBuilder()
        metadataRoot.Serialize(metadataBlob, 0, 0)

        { Metadata = metadataBlob.ToArray()
          EncLog = encLog |> Seq.toArray |> Array.map (fun struct (a, b, c) -> (a, b, c))
          EncMap = encMap |> Seq.toArray |> Array.map (fun struct (a, b) -> (a, b)) }
