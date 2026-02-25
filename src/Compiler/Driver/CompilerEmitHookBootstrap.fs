module internal FSharp.Compiler.CompilerEmitHookBootstrap

open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.HotReloadEmitHook

/// Keep hot reload hook wiring in a single adapter module so option parsing stays
/// independent from hot reload implementation details.
///
/// This wiring is intentionally explicit-only: enabling the compiler flag wires
/// the hook for the current compilation invocation, while ambient/session wiring
/// is owned by the hot reload service lifecycle.
let configureHotReloadEmitHook (tcConfigB: TcConfigBuilder) =
    tcConfigB.compilerEmitHook <- Some hotReloadCompilerEmitHook
