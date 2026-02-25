namespace FSharp.Compiler.Service.Tests.HotReload

open System
open Xunit

open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.HotReload.DeltaBuilder
open FSharp.Compiler.HotReload.SymbolChanges
open FSharp.Compiler.HotReloadBaseline
open FSharp.Compiler.TypedTreeDiff

module DeltaBuilderTests =

    let private createBaseline (typeTokens: Map<string, int>) (methodTokens: Map<MethodDefinitionKey, int>) =
        let metadataSnapshot: MetadataSnapshot =
            { HeapSizes =
                { StringHeapSize = 64
                  UserStringHeapSize = 32
                  BlobHeapSize = 64
                  GuidHeapSize = 16 }
              TableRowCounts = Array.create 64 0
              GuidHeapStart = 0 }

        { ModuleId = System.Guid.NewGuid()
          EncId = System.Guid.Empty
          EncBaseId = System.Guid.Empty
          NextGeneration = 1
          ModuleNameOffset = None
          Metadata = metadataSnapshot
          TokenMappings =
            { TypeDefTokenMap = fun _ -> 0
              FieldDefTokenMap = fun _ _ -> 0
              MethodDefTokenMap = fun _ _ -> 0
              PropertyTokenMap = fun _ _ -> 0
              EventTokenMap = fun _ _ -> 0 }
          TypeTokens = typeTokens
          MethodTokens = methodTokens
          FieldTokens = Map.empty
          PropertyTokens = Map.empty
          EventTokens = Map.empty
          PropertyMapEntries = Map.empty
          EventMapEntries = Map.empty
          MethodSemanticsEntries = Map.empty
          IlxGenEnvironment = None
          PortablePdb = None
          SynthesizedNameSnapshot = Map.empty
          MetadataHandles =
            { MethodHandles = Map.empty
              ParameterHandles = Map.empty
              PropertyHandles = Map.empty
              EventHandles = Map.empty }
          TypeReferenceTokens = Map.empty
          AssemblyReferenceTokens = Map.empty
          TableEntriesAdded = Array.zeroCreate 64
          StringStreamLengthAdded = 0
          UserStringStreamLengthAdded = 0
          BlobStreamLengthAdded = 0
          GuidStreamLengthAdded = 0
          AddedOrChangedMethods = [] }

    let private mkSymbol
        (path: string list)
        (logicalName: string)
        (stamp: int64)
        (kind: SymbolKind)
        (memberKind: SymbolMemberKind option)
        =
        { SymbolId.Path = path
          LogicalName = logicalName
          Stamp = stamp
          Kind = kind
          MemberKind = memberKind
          IsSynthesized = false
          CompiledName = None
          TotalArgCount = None
          GenericArity = None
          ParameterTypeIdentities = None
          ReturnTypeIdentity = None }

    [<Fact>]
    let ``mapSymbolChangesToDelta resolves nested entity by normalized type path`` () =
        let baselineTypeName = "Sample.Container+Nested"

        let baseline =
            createBaseline
                (Map.ofList [ baselineTypeName, 0x02000002 ])
                Map.empty

        let symbol =
            mkSymbol [ "Sample"; "Container" ] "Nested" 1L SymbolKind.Entity None

        let changes: FSharpSymbolChanges =
            { Added = []
              Updated =
                [ { UpdatedSymbolChange.Symbol = symbol
                    Kind = SemanticEditKind.TypeDefinition
                    ContainingEntity = None } ]
              Deleted = []
              Synthesized = []
              RudeEdits = [] }

        let updatedTypes, updatedMethods, accessorUpdates =
            match mapSymbolChangesToDelta baseline changes with
            | Ok result -> result
            | Error errors -> failwithf "Expected successful mapping, got %A" errors

        Assert.Equal<string list>([ baselineTypeName ], updatedTypes)
        Assert.Empty(updatedMethods)
        Assert.Empty(accessorUpdates)

    [<Fact>]
    let ``mapSymbolChangesToDelta resolves method update when nested type separators differ`` () =
        let baselineTypeName = "Sample.Container+Nested"

        let methodKey: MethodDefinitionKey =
            { DeclaringType = baselineTypeName
              Name = "Run"
              GenericArity = 0
              ParameterTypes = []
              ReturnType = ILType.Void }

        let baseline =
            createBaseline
                (Map.ofList [ baselineTypeName, 0x02000002 ])
                (Map.ofList [ methodKey, 0x06000002 ])

        let methodSymbol =
            { mkSymbol [ "Sample"; "Container"; "Nested" ] "Run" 2L SymbolKind.Value (Some SymbolMemberKind.Method) with
                CompiledName = Some "Run"
                TotalArgCount = Some 0
                GenericArity = Some 0
                ParameterTypeIdentities = Some []
                ReturnTypeIdentity = Some "System.Void" }

        let changes: FSharpSymbolChanges =
            { Added = []
              Updated =
                [ { UpdatedSymbolChange.Symbol = methodSymbol
                    Kind = SemanticEditKind.MethodBody
                    ContainingEntity = None } ]
              Deleted = []
              Synthesized = []
              RudeEdits = [] }

        let updatedTypes, updatedMethods, accessorUpdates =
            match mapSymbolChangesToDelta baseline changes with
            | Ok result -> result
            | Error errors -> failwithf "Expected successful mapping, got %A" errors

        Assert.Empty(updatedTypes)
        Assert.Equal<MethodDefinitionKey list>([ methodKey ], updatedMethods)
        Assert.Empty(accessorUpdates)

    [<Fact>]
    let ``mapSymbolChangesToDelta fails closed on ambiguous method mapping`` () =
        let primaryTypeName = "Sample.Container+Nested"
        let secondaryTypeName = "Container+Nested"

        let primaryMethod: MethodDefinitionKey =
            { DeclaringType = primaryTypeName
              Name = "Run"
              GenericArity = 0
              ParameterTypes = []
              ReturnType = ILType.Void }

        let secondaryMethod: MethodDefinitionKey =
            { DeclaringType = secondaryTypeName
              Name = "Run"
              GenericArity = 0
              ParameterTypes = []
              ReturnType = ILType.Void }

        let baseline =
            createBaseline
                (Map.ofList [ primaryTypeName, 0x02000002
                              secondaryTypeName, 0x02000003 ])
                (Map.ofList [ primaryMethod, 0x06000002
                              secondaryMethod, 0x06000003 ])

        let methodSymbol =
            { mkSymbol [ "Sample"; "Container"; "Nested" ] "Run" 3L SymbolKind.Value (Some SymbolMemberKind.Method) with
                CompiledName = Some "Run"
                TotalArgCount = Some 0
                GenericArity = Some 0
                ParameterTypeIdentities = Some []
                ReturnTypeIdentity = Some "System.Void" }

        let changes: FSharpSymbolChanges =
            { Added = []
              Updated =
                [ { UpdatedSymbolChange.Symbol = methodSymbol
                    Kind = SemanticEditKind.MethodBody
                    ContainingEntity = None } ]
              Deleted = []
              Synthesized = []
              RudeEdits = [] }

        match mapSymbolChangesToDelta baseline changes with
        | Ok _ -> failwith "Expected ambiguous method mapping to fail closed"
        | Error errors ->
            Assert.Contains(errors, fun message -> message.Contains("Ambiguous baseline method mapping", StringComparison.Ordinal))

    [<Fact>]
    let ``mapSymbolChangesToDelta fails closed when runtime method identity is incomplete`` () =
        let baselineTypeName = "Sample.Container+Nested"

        let methodKey: MethodDefinitionKey =
            { DeclaringType = baselineTypeName
              Name = "Run"
              GenericArity = 0
              ParameterTypes = []
              ReturnType = ILType.Void }

        let baseline =
            createBaseline
                (Map.ofList [ baselineTypeName, 0x02000002 ])
                (Map.ofList [ methodKey, 0x06000002 ])

        let methodSymbol =
            { mkSymbol [ "Sample"; "Container"; "Nested" ] "Run" 4L SymbolKind.Value (Some SymbolMemberKind.Method) with
                CompiledName = Some "Run"
                TotalArgCount = Some 0
                GenericArity = Some 0
                ParameterTypeIdentities = None
                ReturnTypeIdentity = Some "System.Void" }

        let changes: FSharpSymbolChanges =
            { Added = []
              Updated =
                [ { UpdatedSymbolChange.Symbol = methodSymbol
                    Kind = SemanticEditKind.MethodBody
                    ContainingEntity = None } ]
              Deleted = []
              Synthesized = []
              RudeEdits = [] }

        match mapSymbolChangesToDelta baseline changes with
        | Ok _ -> failwith "Expected incomplete runtime method identity to fail closed"
        | Error errors ->
            Assert.Contains(errors, fun message -> message.Contains("runtime signature identity is incomplete", StringComparison.Ordinal))
