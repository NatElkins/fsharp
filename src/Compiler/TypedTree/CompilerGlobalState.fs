// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Defines the global environment for all type checking.

module FSharp.Compiler.CompilerGlobalState

open System
open System.Collections.Concurrent
open System.Threading
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.Text
open FSharp.Compiler.SynthesizedTypeMaps
open FSharp.Compiler.GeneratedNames

/// Generates compiler-generated names. Each name generated also includes the StartLine number of the range passed in
/// at the point of first generation.
///
/// This type may be accessed concurrently, though in practice it is only used from the compilation thread.
/// It is made concurrency-safe since a global instance of the type is allocated in tast.fs, and it is good
/// policy to make all globally-allocated objects concurrency safe in case future versions of the compiler
/// are used to host multiple concurrent instances of compilation.
type NiceNameGenerator(getSynthesizedMap: unit -> FSharpSynthesizedTypeMaps option) =
    // Use file path (stable) instead of FileIndex (unstable when files added/removed).
    // Hash the file path to get a stable integer key.
    let basicNameCounts = ConcurrentDictionary<struct (string * int), int ref>(max Environment.ProcessorCount 1, 127)
    // Cache this as a delegate.
    let basicNameCountsAddDelegate = Func<struct (string * int), int ref>(fun _ -> ref 0)

    // FNV-1a hash for stable file path hashing
    let stableFileHash (path: string) =
        let mutable hash = 0x811c9dc5u
        for c in path do
            hash <- hash ^^^ uint32 c
            hash <- hash * 0x01000193u
        int hash

    let ensureOrdinal basicName (m: range) =
        // Use stable hash of file path instead of FileIndex which changes when files added/removed
        let key = struct (basicName, stableFileHash m.FileName)
        let countCell = basicNameCounts.GetOrAdd(key, basicNameCountsAddDelegate)
        let count = Interlocked.Increment(countCell)
        count - 1

    member _.FreshCompilerGeneratedNameOfBasicName (basicName, m: range) =
        match getSynthesizedMap() with
        | Some map ->
            // Maintain internal counters so we fall back consistently when hot reload is disabled.
            let _ = ensureOrdinal basicName m
            map.GetOrAddName basicName
        | None ->
            let ordinal = ensureOrdinal basicName m
            makeHotReloadName basicName ordinal

    member this.FreshCompilerGeneratedName (name, m: range) =
        this.FreshCompilerGeneratedNameOfBasicName (GetBasicNameOfPossibleCompilerGeneratedName name, m)

    member _.IncrementOnly(name: string, m: range) = ensureOrdinal name m

    new () = NiceNameGenerator(fun () -> None)

/// Generates compiler-generated names marked up with a source code location, but if given the same unique value then
/// return precisely the same name. Each name generated also includes the StartLine number of the range passed in
/// at the point of first generation.
///
/// This type may be accessed concurrently, though in practice it is only used from the compilation thread.
/// It is made concurrency-safe since a global instance of the type is allocated in tast.fs.
type StableNiceNameGenerator(getSynthesizedMap: unit -> FSharpSynthesizedTypeMaps option) =

    let niceNames = ConcurrentDictionary<string * int64, string>(max Environment.ProcessorCount 1, 127)
    let innerGenerator = new NiceNameGenerator(getSynthesizedMap)

    member x.GetUniqueCompilerGeneratedName (name, m: range, uniq) =
        let basicName = GetBasicNameOfPossibleCompilerGeneratedName name
        let key = basicName, uniq
        niceNames.GetOrAdd(key, fun (basicName, _) -> innerGenerator.FreshCompilerGeneratedNameOfBasicName(basicName, m))

    new () = StableNiceNameGenerator(fun () -> None)

type internal CompilerGlobalState () =
    /// A global generator of compiler generated names
    let synthesizedTypeMapsLock = obj ()
    let mutable synthesizedTypeMaps: FSharpSynthesizedTypeMaps option = None

    let getSynthesizedMap () =
        lock synthesizedTypeMapsLock (fun () -> synthesizedTypeMaps)

    let globalNng = NiceNameGenerator(getSynthesizedMap)

    /// A global generator of stable compiler generated names
    let globalStableNameGenerator = StableNiceNameGenerator(getSynthesizedMap)

    /// A name generator used by IlxGen for static fields, some generated arguments and other things.
    let ilxgenGlobalNng = NiceNameGenerator(getSynthesizedMap)

    member _.NiceNameGenerator = globalNng

    member _.StableNameGenerator = globalStableNameGenerator

    member _.IlxGenNiceNameGenerator = ilxgenGlobalNng

    member _.SynthesizedTypeMaps
        with get () = lock synthesizedTypeMapsLock (fun () -> synthesizedTypeMaps)
        and set value = lock synthesizedTypeMapsLock (fun () -> synthesizedTypeMaps <- value)

/// Unique name generator for stamps attached to lambdas and object expressions
type Unique = int64

//++GLOBAL MUTABLE STATE (concurrency-safe)
let mutable private uniqueCount = 0L
let newUnique() = Interlocked.Increment &uniqueCount

/// Unique name generator for stamps attached to to val_specs, tycon_specs etc.
//++GLOBAL MUTABLE STATE (concurrency-safe)
let mutable private stampCount = 0L
let newStamp() =
    let stamp = Interlocked.Increment &stampCount
    stamp
