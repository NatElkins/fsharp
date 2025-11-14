namespace SampleJson

open System
open System.Text.Json

// Instantiate the provided type based on the Order record defined above
 type OrderJson = Fs1023Json.JsonSerializerProvider<Source = SampleJson.Order>

module Program =
    [<EntryPoint>]
    let main _ =
        let original =
            { Order.Id = 42
              Customer = "Ada"
              Items = [ "Apples"; "Bananas" ] }

        let json = OrderJson.ToJson original
        let clone = OrderJson.FromJson json

        printfn "Original: %A" original
        printfn "JSON: %s" json
        printfn "Clone equals original? %b" (clone = original)
        0
