#r "./artifacts/bin/FSharp.Compiler.Service/Debug/net10.0/FSharp.Compiler.Service.dll"
open FSharp.Compiler.Service.Tests.HotReload.MetadataDeltaTestHelpers
let artifacts = emitPropertyDeltaArtifacts None ()
printfn "String heap len: %d" artifacts.Delta.StringHeap.Length
printfn "Blob heap len: %d" artifacts.Delta.BlobHeap.Length
