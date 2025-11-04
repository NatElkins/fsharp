module internal FSharp.Compiler.CodeGen.FSharpDeltaMetadataWriter

open System
open System.Collections.Generic
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FSharp.Compiler.AbstractIL.ILBinaryWriter
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

let emit
    (metadataReader: MetadataReader)
    (baselineSnapshot: MetadataSnapshot)
    (encId: Guid)
    (encBaseId: Guid)
    (moduleId: Guid)
    (updates: MethodMetadataUpdate list)
    : MetadataDelta =
    if List.isEmpty updates then
        { Metadata = Array.empty
          EncLog = Array.empty
          EncMap = Array.empty }
    else
        let heapSizes = baselineSnapshot.HeapSizes
        let metadataBuilder =
            MetadataBuilder(
                userStringHeapStartOffset = heapSizes.UserStringHeapSize,
                stringHeapStartOffset = heapSizes.StringHeapSize,
                blobHeapStartOffset = heapSizes.BlobHeapSize,
                guidHeapStartOffset = baselineSnapshot.GuidHeapStart
            )

        // Ensure tables not emitted in the current delta remain empty to satisfy metadata writer invariants.
        let methodUpdateCount = updates |> List.length

        metadataBuilder.SetCapacity(TableIndex.Module, 1)
        metadataBuilder.SetCapacity(TableIndex.TypeRef, 0)
        metadataBuilder.SetCapacity(TableIndex.TypeDef, 0)
        metadataBuilder.SetCapacity(TableIndex.Field, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodDef, methodUpdateCount)
        metadataBuilder.SetCapacity(TableIndex.Param, 0)
        metadataBuilder.SetCapacity(TableIndex.InterfaceImpl, 0)
        metadataBuilder.SetCapacity(TableIndex.MemberRef, 0)
        metadataBuilder.SetCapacity(TableIndex.Constant, 0)
        metadataBuilder.SetCapacity(TableIndex.CustomAttribute, 0)
        metadataBuilder.SetCapacity(TableIndex.FieldMarshal, 0)
        metadataBuilder.SetCapacity(TableIndex.DeclSecurity, 0)
        metadataBuilder.SetCapacity(TableIndex.ClassLayout, 0)
        metadataBuilder.SetCapacity(TableIndex.FieldLayout, 0)
        metadataBuilder.SetCapacity(TableIndex.StandAloneSig, 0)
        metadataBuilder.SetCapacity(TableIndex.EventMap, 0)
        metadataBuilder.SetCapacity(TableIndex.Event, 0)
        metadataBuilder.SetCapacity(TableIndex.PropertyMap, 0)
        metadataBuilder.SetCapacity(TableIndex.Property, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodSemantics, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodImpl, 0)
        metadataBuilder.SetCapacity(TableIndex.ModuleRef, 0)
        metadataBuilder.SetCapacity(TableIndex.TypeSpec, 0)
        metadataBuilder.SetCapacity(TableIndex.ImplMap, 0)
        metadataBuilder.SetCapacity(TableIndex.FieldRva, 0)
        let encEntryCount = methodUpdateCount + 1
        metadataBuilder.SetCapacity(TableIndex.EncLog, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.EncMap, encEntryCount)
        metadataBuilder.SetCapacity(TableIndex.Assembly, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyOS, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRef, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefProcessor, 0)
        metadataBuilder.SetCapacity(TableIndex.AssemblyRefOS, 0)
        metadataBuilder.SetCapacity(TableIndex.File, 0)
        metadataBuilder.SetCapacity(TableIndex.ExportedType, 0)
        metadataBuilder.SetCapacity(TableIndex.ManifestResource, 0)
        metadataBuilder.SetCapacity(TableIndex.NestedClass, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParam, 0)
        metadataBuilder.SetCapacity(TableIndex.MethodSpec, 0)
        metadataBuilder.SetCapacity(TableIndex.GenericParamConstraint, 0)

        let moduleDef = metadataReader.GetModuleDefinition()
        let moduleName = metadataReader.GetString moduleDef.Name
        let moduleNameHandle = metadataBuilder.GetOrAddString(moduleName)
        let mvidHandle = metadataBuilder.GetOrAddGuid(moduleId)
        let encIdHandle = metadataBuilder.GetOrAddGuid(encId)
        let encBaseHandle = metadataBuilder.GetOrAddGuid(encBaseId)
        let moduleHandle = metadataBuilder.AddModule(0, moduleNameHandle, mvidHandle, encIdHandle, encBaseHandle)

        // Sort method updates by baseline row id to produce deterministic ordering.
        let orderedUpdates =
            updates
            |> List.sortBy (fun u -> MetadataTokens.GetRowNumber(u.MethodHandle))

        let mutable encLog = ResizeArray()
        let mutable encMap = ResizeArray()

        metadataBuilder.AddEncLogEntry(moduleHandle, EditAndContinueOperation.Default) |> ignore
        metadataBuilder.AddEncMapEntry(moduleHandle) |> ignore
        let moduleRowId = MetadataTokens.GetRowNumber moduleHandle
        encLog.Add(struct (TableIndex.Module, moduleRowId, EditAndContinueOperation.Default))
        encMap.Add(struct (TableIndex.Module, moduleRowId))

        for update in orderedUpdates do
            let methodDef = metadataReader.GetMethodDefinition update.MethodHandle

            let methodName = metadataReader.GetString methodDef.Name
            let nameHandle = metadataBuilder.GetOrAddString methodName

            let signatureBytes = metadataReader.GetBlobBytes methodDef.Signature
            let signatureHandle = metadataBuilder.GetOrAddBlob signatureBytes

            metadataBuilder.AddMethodDefinition(
                methodDef.Attributes,
                methodDef.ImplAttributes,
                nameHandle,
                signatureHandle,
                update.Body.CodeOffset,
                ParameterHandle()
            ) |> ignore

            let rowId = update.MethodToken &&& 0x00FFFFFF
            let methodHandle = MetadataTokens.MethodDefinitionHandle update.MethodToken
            metadataBuilder.AddEncLogEntry(methodHandle, EditAndContinueOperation.Default) |> ignore
            metadataBuilder.AddEncMapEntry(methodHandle) |> ignore
            encLog.Add(struct (TableIndex.MethodDef, rowId, EditAndContinueOperation.Default))
            encMap.Add(struct (TableIndex.MethodDef, rowId))

        let debugRows =
            [ for index in Enum.GetValues(typeof<TableIndex>) |> Seq.cast<TableIndex> do
                  let count = metadataBuilder.GetRowCount index
                  if count <> 0 then yield index, count ]

        let allowedTables =
            set [ TableIndex.Module; TableIndex.MethodDef; TableIndex.EncLog; TableIndex.EncMap ]

        let unexpectedTables =
            debugRows
            |> List.filter (fun (index, _) -> not (allowedTables.Contains index))

        if not (List.isEmpty unexpectedTables) then
            let details =
                unexpectedTables
                |> List.map (fun (index, count) -> sprintf "%A:%d" index count)
                |> String.concat ", "
            failwithf "Unexpected rows in delta metadata: %s" details

        let metadataRoot = new MetadataRootBuilder(metadataBuilder)
        let metadataBlob = BlobBuilder()
        try
            metadataRoot.Serialize(metadataBlob, 0, 0)
        with ex ->
            let counts =
                [ for index in Enum.GetValues(typeof<TableIndex>) |> Seq.cast<TableIndex> do
                      yield index, metadataBuilder.GetRowCount index ]
                |> List.filter (fun (_, count) -> count <> 0)
            let details = counts |> List.map (fun (i, c) -> sprintf "%A:%d" i c) |> String.concat ", "
            let enriched = sprintf "Metadata serialization failed. Non-zero tables: %s" details
            raise (Exception(enriched, ex))

        { Metadata = metadataBlob.ToArray()
          EncLog = encLog |> Seq.toArray |> Array.map (fun struct (a, b, c) -> (a, b, c))
          EncMap = encMap |> Seq.toArray |> Array.map (fun struct (a, b) -> (a, b)) }
