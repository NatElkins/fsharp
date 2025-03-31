namespace FSharp.Compiler.ExpressionEvaluator

module Say =
    let hello name =
        printfn "Hello %s" name
