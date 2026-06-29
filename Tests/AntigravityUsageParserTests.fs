module CodexBarWin.Core.Tests.AntigravityUsageParserTests

open System
open System.Text.Json
open Xunit
open CodexBarWin.Core

let private parseJson (s: string) =
    use doc = JsonDocument.Parse(s)
    AntigravityUsageParser.parse doc.RootElement

[<Fact>]
let ``Empty payload returns no buckets`` () =
    let r = parseJson "{}"
    Assert.Empty(r)

[<Fact>]
let ``Missing buckets array returns no buckets`` () =
    let r = parseJson """{ "otherField": "value" }"""
    Assert.Empty(r)

[<Fact>]
let ``Single bucket with modelId parses correctly`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let json = sprintf """{ "buckets": [{ "modelId": "gemini-2.5-pro", "remainingFraction": 0.45, "resetTime": "%s" }] }""" reset
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("gemini-2.5-pro", b.ModelId)
    Assert.Equal(0.45, b.RemainingFraction, 4)
    Assert.Equal(55.0, b.UsedPercent, 1)
    Assert.StartsWith("3h", b.ResetCountdown)  // DateParser emits "3h 59m" or "4h 0m" depending on rounding

[<Fact>]
let ``Bucket without modelId parses as empty modelId`` () =
    let json = """{ "buckets": [{ "remainingFraction": 0.75, "resetTime": "2030-01-01T00:00:00Z" }] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("", b.ModelId)
    Assert.Equal(25.0, b.UsedPercent, 1)

[<Fact>]
let ``Multiple buckets are preserved in order`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket1 = sprintf """{ "modelId": "gemini-2.5-pro", "remainingFraction": 0.20, "resetTime": "%s" }""" reset
    let bucket2 = sprintf """{ "modelId": "gemini-2.5-flash", "remainingFraction": 0.80, "resetTime": "%s" }""" reset
    let bucket3 = sprintf """{ "modelId": "claude-sonnet-4", "remainingFraction": 0.50, "resetTime": "%s" }""" reset
    let json = "{ \"buckets\": [" + bucket1 + ", " + bucket2 + ", " + bucket3 + "] }"
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(3, List.length r)
    let labels = r |> List.map (fun b -> b.ModelId)
    Assert.Equal<string list>(["gemini-2.5-pro"; "gemini-2.5-flash"; "claude-sonnet-4"], labels)

[<Fact>]
let ``Bucket with missing remainingFraction is dropped`` () =
    let json = """{ "buckets": [
        { "modelId": "good", "remainingFraction": 0.50, "resetTime": "2030-01-01T00:00:00Z" },
        { "modelId": "bad", "resetTime": "2030-01-01T00:00:00Z" }
    ] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("good", b.ModelId)

[<Fact>]
let ``Bucket with non-numeric remainingFraction is dropped`` () =
    let json = """{ "buckets": [
        { "modelId": "good", "remainingFraction": 0.50, "resetTime": "2030-01-01T00:00:00Z" },
        { "modelId": "bad", "remainingFraction": "not-a-number", "resetTime": "2030-01-01T00:00:00Z" }
    ] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("good", b.ModelId)

[<Fact>]
let ``Bucket with missing resetTime defaults to Never Resets`` () =
    let json = """{ "buckets": [{ "modelId": "x", "remainingFraction": 0.50 }] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("Never Resets", b.ResetCountdown)

[<Fact>]
let ``Bucket with empty resetTime defaults to Never Resets`` () =
    let json = """{ "buckets": [{ "modelId": "x", "remainingFraction": 0.50, "resetTime": "" }] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal("Never Resets", b.ResetCountdown)

[<Fact>]
let ``Used percent is clamped to 100`` () =
    let json = """{ "buckets": [{ "modelId": "x", "remainingFraction": -0.5, "resetTime": "2030-01-01T00:00:00Z" }] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal(100.0, b.UsedPercent)

[<Fact>]
let ``Used percent is clamped to 0`` () =
    let json = """{ "buckets": [{ "modelId": "x", "remainingFraction": 1.5, "resetTime": "2030-01-01T00:00:00Z" }] }"""
    let r: AntigravityUsageParser.Bucket list = parseJson json
    Assert.Equal(1, List.length r)
    let b = r |> List.head
    Assert.Equal(0.0, b.UsedPercent)
