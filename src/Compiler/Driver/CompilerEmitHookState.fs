module internal FSharp.Compiler.CompilerEmitHookState

open FSharp.Compiler.CompilerConfig

/// Ambient hook state is isolated from CompilerConfig so the core config contract stays stable.
let mutable private ambientCompilerEmitHook: ICompilerEmitHook option = None

/// Register an ambient emit hook for follow-up compiler invocations in the same process.
let setAmbientCompilerEmitHook (hook: ICompilerEmitHook) =
    ambientCompilerEmitHook <- Some hook

/// Clear the ambient emit hook registration.
let clearAmbientCompilerEmitHook () =
    ambientCompilerEmitHook <- None

/// Resolve the emit hook from explicit config first, then ambient registration, then no-op default.
let resolveCompilerEmitHook (explicitHook: ICompilerEmitHook option) =
    explicitHook
    |> Option.orElse ambientCompilerEmitHook
    |> Option.defaultValue defaultCompilerEmitHook
