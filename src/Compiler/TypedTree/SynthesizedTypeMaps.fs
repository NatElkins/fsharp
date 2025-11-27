module internal FSharp.Compiler.SynthesizedTypeMaps

open System.Collections.Concurrent
open System.Collections.Generic

open FSharp.Compiler.GeneratedNames

/// <summary>Provides stable compiler-generated names across hot reload sessions.</summary>
type FSharpSynthesizedTypeMaps() =
    let syncLock = obj ()
    let buckets = ConcurrentDictionary<string, ResizeArray<string>>()
    let ordinals = ConcurrentDictionary<string, int>()

    let createBucket (names: string[]) =
        let bucket = ResizeArray<string>()
        for name in names do
            bucket.Add(name)
        bucket

    let computeName basicName index =
        makeHotReloadName basicName index

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
        lock syncLock (fun () ->
            for KeyValue(key, _) in buckets do
                ordinals[key] <- 0)

    /// <summary>Captures the current stable names grouped by compiler-generated base name.</summary>
    member _.Snapshot: seq<string * string[]> =
        lock syncLock (fun () ->
            // Materialize the snapshot under the lock to avoid race conditions
            [| for KeyValue(key, bucket) in buckets do yield key, bucket.ToArray() |]
            :> seq<string * string[]>)

    /// <summary>Loads a previously captured snapshot, replacing any existing allocation state.</summary>
    member _.LoadSnapshot(snapshot: seq<string * string[]>) =
        lock syncLock (fun () ->
            buckets.Clear()
            ordinals.Clear()

            for (basicName, names) in snapshot do
                let bucket = createBucket names
                buckets[basicName] <- bucket
                ordinals[basicName] <- 0)

/// <summary>Retrieves a stable compiler-generated name or falls back to the provided generator.</summary>
let nextName mapOpt basicName generate =
    match mapOpt with
    | Some(map: FSharpSynthesizedTypeMaps) -> map.GetOrAddName basicName
    | None -> generate ()
