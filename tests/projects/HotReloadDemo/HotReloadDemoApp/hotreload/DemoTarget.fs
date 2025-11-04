namespace HotReloadDemo.Target

module Demo =
    let mutable private counter = 0

    let GetMessage() =
        counter <- counter + 1
        $"Hello from generation 1 (invocation #{counter})"
