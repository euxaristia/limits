module Limits.Core.Tests.AntigravityUsageParserTests

open System
open System.Text.Json
open Xunit
open Limits.Core

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
let ``Single Gemini bucket parses correctly`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let json = sprintf """{ "buckets": [ { "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.45, "resetTime": "%s", "tokenType": "WTUS" } ] }""" reset
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini", b.GroupLabel)
    Assert.Equal("gemini-3-1-pro-low", b.Members)
    Assert.Equal(55.0, b.UsedPercent, 1)
    Assert.Equal(45.0, b.RemainingPercent, 1)
    Assert.StartsWith("3h", b.ResetCountdown)

[<Fact>]
let ``Multiple Gemini models with same resetTime group into one bucket`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let b1 = sprintf """{ "modelId": "gemini-2-5-pro", "remainingFraction": 0.50, "resetTime": "%s" }""" reset
    let b2 = sprintf """{ "modelId": "gemini-3-1-flash-lite", "remainingFraction": 0.20, "resetTime": "%s" }""" reset
    let json = "{ \"buckets\": [ " + b1 + ", " + b2 + " ] }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini", b.GroupLabel)
    Assert.Equal(2, b.Members.Split(',').Length)
    // Group remaining = min(0.50, 0.20) = 0.20
    Assert.Equal(20.0, b.RemainingPercent, 1)
    Assert.Equal(80.0, b.UsedPercent, 1)

[<Fact>]
let ``Gemini 5h and Weekly resetTimes form two separate buckets`` () =
    let reset5h = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let resetWeek = DateTime.UtcNow.AddDays(5.0).ToString("o")
    let b1 = sprintf """{ "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.99, "resetTime": "%s" }""" reset5h
    let b2 = sprintf """{ "modelId": "gemini-3-1-pro-high", "remainingFraction": 0.28, "resetTime": "%s" }""" resetWeek
    let json = "{ \"buckets\": [ " + b1 + ", " + b2 + " ] }"
    let r = parseJson json
    Assert.Equal(2, List.length r)
    let groupLabels = r |> List.map (fun b -> b.GroupLabel) |> Set.ofList
    Assert.Single(groupLabels)
    Assert.Equal(2, List.length r)
    let countdowns = r |> List.map (fun b -> b.ResetCountdown) |> String.concat "|"
    Assert.Contains("h ", countdowns)  // at least one short countdown
    Assert.True(countdowns.Contains("d ") || countdowns.Length > 5)

[<Fact>]
let ``Gemini and Anthropic models form separate family groups`` () =
    let resetGem = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let resetAnt = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let gem = sprintf """{ "modelId": "gemini-3-1-pro", "remainingFraction": 0.50, "resetTime": "%s" }""" resetGem
    let ant = sprintf """{ "modelId": "claude-sonnet-4-6", "remainingFraction": 0.0, "resetTime": "%s" }""" resetAnt
    let json = "{ \"buckets\": [ " + gem + ", " + ant + " ] }"
    let r = parseJson json
    Assert.Equal(2, List.length r)
    let labels = r |> List.map (fun b -> b.GroupLabel) |> Set.ofList
    Assert.True(Set.contains "Gemini" labels)
    Assert.True(Set.contains "Claude & GPT" labels)

[<Fact>]
let ``Anthropic and OpenAI both fall into Claude & GPT family`` () =
    let reset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let ant = sprintf """{ "modelId": "claude-opus-4-6-thinking", "remainingFraction": 0.0, "resetTime": "%s" }""" reset
    let gpt = sprintf """{ "modelId": "gpt-oss-120b-medium", "remainingFraction": 0.0, "resetTime": "%s" }""" reset
    let json = "{ \"buckets\": [ " + ant + ", " + gpt + " ] }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Claude & GPT", b.GroupLabel)
    Assert.Equal(2, b.Members.Split(',').Length)

[<Fact>]
let ``Placeholder models (chat_*, tab_*) are filtered out`` () =
    let reset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let b1 = sprintf """{ "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.50, "resetTime": "%s" }""" reset
    let b2 = """{ "modelId": "chat_23310", "remainingFraction": 1.0 }"""
    let b3 = """{ "modelId": "tab_flash_lite_preview", "remainingFraction": 1.0 }"""
    let json = "{ \"buckets\": [ " + b1 + ", " + b2 + ", " + b3 + " ] }"
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Gemini", b.GroupLabel)
    Assert.Equal(1, b.Members.Split(',').Length)

[<Fact>]
let ``Bucket with no resetTime is treated as 0% remaining (limit hit)`` () =
    // The API returns chat_*/tab_* placeholders with no resetTime. But
    // since those are filtered, this test covers a non-placeholder model
    // with a missing resetTime - which the API also returns for some
    // models when the server hasn't computed a reset.
    let json = """{ "buckets": [ { "modelId": "claude-opus-4-6", "remainingFraction": 0.0 } ] }"""
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("Claude & GPT", b.GroupLabel)
    Assert.Equal(0.0, b.RemainingPercent)

[<Fact>]
let ``Bucket without modelId is dropped`` () =
    let json = """{ "buckets": [ { "remainingFraction": 0.5 }, { "modelId": "gemini-3-1-pro", "remainingFraction": 0.7 } ] }"""
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal("gemini-3-1-pro", b.Members)

[<Fact>]
let ``Used percent is clamped to 0..100`` () =
    let json = """{ "buckets": [
        { "modelId": "gemini-test-a", "remainingFraction": -0.5 },
        { "modelId": "gemini-test-b", "remainingFraction": 1.5 }
    ] }"""
    let r = parseJson json
    Assert.Single(r)
    let b = r |> List.head
    Assert.Equal(2, b.Members.Split(',').Length)
    Assert.InRange(b.UsedPercent, 0.0, 100.0)
    Assert.InRange(b.RemainingPercent, 0.0, 100.0)

[<Fact>]
let ``Real Antigravity response shape parses into 2 Gemini + 2 Claude/GPT buckets`` () =
    // Mimics the real retrieveUserQuota response: 3 Gemini buckets all
    // sharing the 5h reset, 3 Claude/GPT buckets all sharing the weekly
    // reset, plus 2 chat_/tab_ placeholders (filtered out).
    let reset5h = "2026-06-30T11:56:23Z"
    let resetWeek = "2026-07-01T01:54:33Z"
    let gem1 = sprintf """{ "modelId": "gemini-2-5-pro", "remainingFraction": 0.97, "resetTime": "%s", "tokenType": "WTUS" }""" reset5h
    let gem2 = sprintf """{ "modelId": "gemini-3-1-pro-low", "remainingFraction": 0.97, "resetTime": "%s", "tokenType": "WTUS" }""" reset5h
    let gem3 = sprintf """{ "modelId": "gemini-3-1-pro-high", "remainingFraction": 0.97, "resetTime": "%s", "tokenType": "WTUS" }""" reset5h
    let claude1 = sprintf """{ "modelId": "claude-sonnet-4-6", "remainingFraction": 0, "resetTime": "%s", "tokenType": "WTUS" }""" resetWeek
    let claude2 = sprintf """{ "modelId": "claude-opus-4-6-thinking", "remainingFraction": 0, "resetTime": "%s", "tokenType": "WTUS" }""" resetWeek
    let gpt1 = sprintf """{ "modelId": "gpt-oss-120b-medium", "remainingFraction": 0, "resetTime": "%s", "tokenType": "WTUS" }""" resetWeek
    let json = "{ \"buckets\": [ " + gem1 + ", " + gem2 + ", " + gem3 + ", " + claude1 + ", " + claude2 + ", " + gpt1 + ", { \"modelId\": \"chat_23310\", \"remainingFraction\": 1, \"tokenType\": \"WTUS\" }, { \"modelId\": \"tab_flash_lite_preview\", \"remainingFraction\": 1, \"tokenType\": \"WTUS\" } ] }"
    let r = parseJson json
    // 2 groups: Gemini 5h, Claude & GPT weekly. chat_/tab_ filtered.
    Assert.Equal(2, List.length r)
    let gem = r |> List.find (fun b -> b.GroupLabel = "Gemini")
    let cgpt = r |> List.find (fun b -> b.GroupLabel = "Claude & GPT")
    Assert.Equal(3, gem.Members.Split(',').Length)
    Assert.Equal(3, cgpt.Members.Split(',').Length)
    Assert.True(gem.RemainingPercent > 90.0, "Gemini 5h should be >90% remaining")
    Assert.Equal(0.0, cgpt.RemainingPercent)
