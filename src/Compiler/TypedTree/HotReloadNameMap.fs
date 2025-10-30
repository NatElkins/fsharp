module internal FSharp.Compiler.HotReloadNameMap

open System.Collections.Concurrent
open System.Collections.Generic

open FSharp.Compiler.Syntax.PrettyNaming

/// <summary>Provides stable compiler-generated names across hot reload sessions.</summary>
type HotReloadNameMap() =
    let buckets = ConcurrentDictionary<string, ResizeArray<string>>()
    let ordinals = ConcurrentDictionary<string, int>()

    let computeName basicName index =
        let suffix =
            if index = 0 then
                "hotreload"
            else
                $"hotreload-{index}"

        CompilerGeneratedNameSuffix basicName suffix

    member _.GetOrAddName(basicName: string) =
        let bucket = buckets.GetOrAdd(basicName, fun _ -> ResizeArray())
        let nextOrdinal = ordinals.AddOrUpdate(basicName, 1, fun _ value -> value + 1)
        let index = nextOrdinal - 1

        lock bucket (fun () ->
            if index < bucket.Count then
                bucket[index]
            else
                let name = computeName basicName index
                bucket.Add(name)
                name)

    /// <summary>Resets allocation state so subsequent edits reuse the original name ordering.</summary>
    member _.BeginSession() =
        for KeyValue(key, _) in buckets do
            ordinals[key] <- 0

    /// <summary>Captures the current stable names grouped by compiler-generated base name.</summary>
    member _.Snapshot: seq<string * string[]> =
        seq {
            for KeyValue(key, bucket) in buckets do
                yield key, bucket.ToArray()
        }

/// <summary>Retrieves a stable compiler-generated name or falls back to the provided generator.</summary>
let nextName mapOpt basicName generate =
    match mapOpt with
    | Some (map: HotReloadNameMap) -> map.GetOrAddName basicName
    | None -> generate()
