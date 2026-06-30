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
let ``Missing models object returns no buckets`` () =
    let r = parseJson """{ "otherField": "value" }"""
    Assert.Empty(r)

[<Fact>]
let ``Single Gemini model with displayName and quotaInfo groups correctly`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let json = sprintf """{ "models": { "gemini-3-1-pro-low": { "displayName": "Gemini 3.1 Pro (Low)", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.45, "resetTime": "%s" } } } }""" reset
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini", b.GroupLabel)
    Assert.Equal("Gemini 3.1 Pro (Low)", b.PrimaryModel)
    Assert.Equal("Gemini 3.1 Pro (Low)", b.Members)
    Assert.Equal(55.0, b.UsedPercent, 1)
    Assert.Equal(45.0, b.RemainingPercent, 1)
    Assert.StartsWith("3h", b.ResetCountdown)

[<Fact>]
let ``Multiple Gemini models in same group share quota`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket1 = sprintf """{ "displayName": "Gemini 2.5 Pro", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset
    let bucket2 = sprintf """{ "displayName": "Gemini 2.5 Flash", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.20, "resetTime": "%s" } }""" reset
    let json = "{ \"models\": { \"a\": " + bucket1 + ", \"b\": " + bucket2 + " } }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini", b.GroupLabel)
    Assert.Equal(2, b.Members.Split(',').Length)
    Assert.Equal(20.0, b.RemainingPercent, 1)
    Assert.Equal(80.0, b.UsedPercent, 1)
    Assert.Equal("Gemini 2.5 Flash", b.PrimaryModel)

[<Fact>]
let ``Gemini and Anthropic models form separate groups`` () =
    let resetGem = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let resetAnt = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let gem = sprintf """{ "displayName": "Gemini Pro", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" resetGem
    let ant = sprintf """{ "displayName": "Claude Sonnet", "modelProvider": "MODEL_PROVIDER_ANTHROPIC", "quotaInfo": { "remainingFraction": 0.0, "resetTime": "%s" } }""" resetAnt
    let json = "{ \"models\": { \"g\": " + gem + ", \"a\": " + ant + " } }"
    let r = parseJson json
    Assert.Equal(2, List.length r)
    let labels = r |> List.map (fun b -> b.GroupLabel) |> Set.ofList
    Assert.True(Set.contains "Gemini" labels)
    Assert.True(Set.contains "Claude & GPT" labels)

[<Fact>]
let ``Placeholder models (chat_*, tab_*, MODEL_PLACEHOLDER_*) are filtered`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket1 = sprintf """{ "displayName": "Gemini 2.5 Pro", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset
    let bucket2 = sprintf """{ "displayName": "Chat Bot", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 1.0, "resetTime": "%s" } }""" reset
    let bucket3 = sprintf """{ "displayName": "Tab Preview", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 1.0, "resetTime": "%s" } }""" reset
    let bucket4 = sprintf """{ "displayName": "Internal", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 1.0, "resetTime": "%s" } }""" reset
    let json = "{ \"models\": { \"a\": " + bucket1 + ", \"b\": " + bucket2 + ", \"c\": " + bucket3 + ", \"d\": " + bucket4 + " } }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini 2.5 Pro", b.PrimaryModel)

[<Fact>]
let ``Group with no remainingFraction is treated as fully consumed`` () =
    let reset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let json = sprintf """{ "models": { "claude-opus": { "displayName": "Claude Opus", "modelProvider": "MODEL_PROVIDER_ANTHROPIC", "quotaInfo": { "resetTime": "%s" } } } }""" reset
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Claude & GPT", b.GroupLabel)
    Assert.Equal(0.0, b.RemainingPercent)
    Assert.Equal(100.0, b.UsedPercent)

[<Fact>]
let ``Model without quotaInfo is dropped`` () =
    let json = """{ "models": { "good": { "displayName": "Good", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.5 } }, "bad": { "displayName": "Bad", "modelProvider": "MODEL_PROVIDER_GOOGLE" } } }"""
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Good", b.PrimaryModel)

[<Fact>]
let ``Used percent is clamped to 0..100`` () =
    let json = """{ "models": {
        "a": { "displayName": "A", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": -0.5 } },
        "b": { "displayName": "B", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 1.5 } }
    } }"""
    let r = parseJson json
    // Both are Google models with no resetTime, so they group into one bucket.
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal(2, b.Members.Split(',').Length)
    Assert.InRange(b.UsedPercent, 0.0, 100.0)
    Assert.InRange(b.RemainingPercent, 0.0, 100.0)

[<Fact>]
let ``Same provider but different resetTimes form separate groups`` () =
    let reset5h = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let resetWeek = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let bucket1 = sprintf """{ "displayName": "Gemini Flash", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset5h
    let bucket2 = sprintf """{ "displayName": "Gemini Pro", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.20, "resetTime": "%s" } }""" resetWeek
    let json = "{ \"models\": { \"a\": " + bucket1 + ", \"b\": " + bucket2 + " } }"
    let r = parseJson json
    Assert.Equal(2, List.length r)
    let groupLabels = r |> List.map (fun b -> b.GroupLabel) |> Set.ofList
    Assert.Single(groupLabels)
    // Both are Gemini, so one unique GroupLabel but two groups (different
    // resetCountdowns).
    Assert.Equal(2, List.length r)

[<Fact>]
let ``Gemini 2.x models are filtered out`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket = sprintf """{ "displayName": "Gemini 2.5 Pro", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset
    let json = "{ \"models\": { \"gemini-2-5-pro\": " + bucket + " } }"
    let r = parseJson json
    Assert.Empty(r)

[<Fact>]
let ``Gemini image variants are filtered out`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket = sprintf """{ "displayName": "Gemini 3.1 Flash Image", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset
    let json = "{ \"models\": { \"gemini-3-1-flash-image\": " + bucket + " } }"
    let r = parseJson json
    Assert.Empty(r)

[<Fact>]
let ``Duplicate displayName within a group is deduped to one entry`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let bucket1 = sprintf """{ "displayName": "Gemini 3.1 Flash Lite", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.50, "resetTime": "%s" } }""" reset
    let bucket2 = sprintf """{ "displayName": "Gemini 3.1 Flash Lite", "modelProvider": "MODEL_PROVIDER_GOOGLE", "quotaInfo": { "remainingFraction": 0.30, "resetTime": "%s" } }""" reset
    let json = "{ \"models\": { \"a\": " + bucket1 + ", \"b\": " + bucket2 + " } }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal(1, b.Members.Split(',').Length)
    // Group's remaining = min(0.50, 0.30) = 0.30
    Assert.Equal(30.0, b.RemainingPercent, 1)
