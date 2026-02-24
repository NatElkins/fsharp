module internal FSharp.Compiler.CompilerEmitHookBootstrap

open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.HotReloadEmitHook

/// Keep hot reload hook wiring in a single adapter module so option parsing stays
/// independent from hot reload implementation details.
let configureHotReloadEmitHook (tcConfigB: TcConfigBuilder) =
    tcConfigB.compilerEmitHook <- Some hotReloadCompilerEmitHook
    // Keep the hot reload hook available for follow-up emits in the same process,
    // even when those invocations omit --enable:hotreloaddeltas.
    setAmbientCompilerEmitHook hotReloadCompilerEmitHook
