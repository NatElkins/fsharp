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

    [<Event(2, Level = EventLevel.Informational, Message = "Provided type emitted: {0}")>]
    member this.ProvidedTypeEmitted(typeName: string, methodCount: int, propertyCount: int, eventCount: int) =
        if base.IsEnabled() then
            base.WriteEvent(2, typeName, methodCount, propertyCount, eventCount)

module internal Fs1023Telemetry =
    let tryWrite (message: string) =
        if String.IsNullOrEmpty(message) then
            ()
        else
            try
                Fs1023EventSource.Instance.Trace(message)
            with _ -> ()

    let providedTypeEmitted typeName methodCount propertyCount eventCount =
        try
            Fs1023EventSource.Instance.ProvidedTypeEmitted(typeName, methodCount, propertyCount, eventCount)
        with _ -> ()

module internal Fs1023TraceControl =
    let private parseEnvSetting () =
        match Environment.GetEnvironmentVariable("FS1023_TRACE") with
        | null
        | "" -> None
        | value when value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase) -> Some false
        | _ -> Some true

    let mutable private commandLineOverride: bool option = None
    let mutable private checkerOverride: bool option = None

    let setCommandLineOverride setting = commandLineOverride <- setting

    let setCheckerOverride setting = checkerOverride <- setting

    let isEnabled () =
        match checkerOverride with
        | Some value -> value
        | None ->
            match commandLineOverride with
            | Some value -> value
            | None -> parseEnvSetting () |> Option.defaultValue false
