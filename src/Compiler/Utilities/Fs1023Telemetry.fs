namespace FSharp.Compiler.Diagnostics

open System
open System.Diagnostics.Tracing

[<EventSource(Name = "FSharpCompiler-FS1023")>]
type internal Fs1023EventSource() =
    inherit EventSource()

    static let instance = lazy (new Fs1023EventSource())

    static member Instance = instance.Value

    [<Event(1, Level = EventLevel.Verbose, Message = "{0}")>]
    member this.Trace(message: string) =
        if base.IsEnabled() then
            base.WriteEvent(1, message)

module internal Fs1023Telemetry =
    let tryWrite (message: string) =
        if String.IsNullOrEmpty(message) then
            ()
        else
            try
                Fs1023EventSource.Instance.Trace(message)
            with _ -> ()
