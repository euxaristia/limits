module Limits.Core.Tests.ClaudeUsageParserTests

open System
open System.Text.Json
open Xunit
open Limits.Core

let private parseJson (s: string) =
    use doc = JsonDocument.Parse(s)
    ClaudeUsageParser.parse doc.RootElement

let private bucket (util: float) (reset: string) = sprintf """{"utilization": %M, "resets_at": "%s"}""" (decimal util) reset

[<Fact>]
let ``Both buckets present: session higher than weekly, session wins as primary`` () =
    let futureReset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let laterReset = DateTime.UtcNow.AddDays(5.0).ToString("o")
    let json = sprintf """{ "five_hour": %s, "seven_day": %s }""" (bucket 0.45 futureReset) (bucket 0.20 laterReset)
    let r = parseJson json
    Assert.Equal(45.0, r.PrimaryUsed, 1)
    Assert.Equal("5-hour session quota", r.PrimaryLabel)
    Assert.Contains("Session: 45%", r.CostInfo)
    Assert.Contains("7-day: 20%", r.CostInfo)

[<Fact>]
let ``Both buckets present: weekly higher than session, weekly wins as primary`` () =
    let futureReset = DateTime.UtcNow.AddHours(2.0).ToString("o")
    let laterReset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let json = sprintf """{ "five_hour": %s, "seven_day": %s }""" (bucket 0.10 futureReset) (bucket 0.87 laterReset)
    let r = parseJson json
    Assert.Equal(87.0, r.PrimaryUsed, 1)
    Assert.Equal("7-day weekly quota", r.PrimaryLabel)
    Assert.Contains("Session: 10%", r.CostInfo)
    Assert.Contains("7-day: 87%", r.CostInfo)

[<Fact>]
let ``Tie between buckets: weekly wins (more informative)`` () =
    let futureReset = DateTime.UtcNow.AddHours(2.0).ToString("o")
    let laterReset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let json = sprintf """{ "five_hour": %s, "seven_day": %s }""" (bucket 0.50 futureReset) (bucket 0.50 laterReset)
    let r = parseJson json
    Assert.Equal(50.0, r.PrimaryUsed, 1)
    Assert.Equal("7-day weekly quota", r.PrimaryLabel)

[<Fact>]
let ``Only session bucket: falls back to session primary`` () =
    let futureReset = DateTime.UtcNow.AddHours(2.0).ToString("o")
    let json = sprintf """{ "five_hour": %s }""" (bucket 0.33 futureReset)
    let r = parseJson json
    Assert.Equal(33.0, r.PrimaryUsed, 1)
    Assert.Equal("5-hour session quota", r.PrimaryLabel)
    Assert.Equal("5-hour session quota", r.CostInfo)

[<Fact>]
let ``Only weekly bucket: falls back to weekly primary`` () =
    let laterReset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let json = sprintf """{ "seven_day": %s }""" (bucket 0.66 laterReset)
    let r = parseJson json
    Assert.Equal(66.0, r.PrimaryUsed, 1)
    Assert.Equal("7-day weekly quota", r.PrimaryLabel)
    Assert.Equal("7-day weekly quota", r.CostInfo)

[<Fact>]
let ``No buckets present: returns safe defaults`` () =
    let r = parseJson "{}"
    Assert.Equal(0.0, r.PrimaryUsed)
    Assert.Equal("Never Resets", r.PrimaryReset)
    Assert.Equal("Claude Plan", r.PrimaryLabel)

[<Fact>]
let ``Null bucket value is treated as missing`` () =
    let json = """{ "five_hour": null, "seven_day": { "utilization": 0.40, "resets_at": "2030-01-01T00:00:00Z" } }"""
    let r = parseJson json
    Assert.Equal(40.0, r.PrimaryUsed, 1)
    Assert.Equal("7-day weekly quota", r.PrimaryLabel)

[<Fact>]
let ``Utilization above 1.0 is treated as already-percent`` () =
    let futureReset = DateTime.UtcNow.AddHours(1.0).ToString("o")
    let json = sprintf """{ "five_hour": %s }""" (bucket 87.5 futureReset)
    let r = parseJson json
    Assert.Equal(87.5, r.PrimaryUsed, 1)

[<Fact>]
let ``Utilization value of 0 is treated as already-percent (boundary)`` () =
    let futureReset = DateTime.UtcNow.AddHours(1.0).ToString("o")
    let json = sprintf """{ "five_hour": %s }""" (bucket 0.0 futureReset)
    let r = parseJson json
    Assert.Equal(0.0, r.PrimaryUsed)

[<Fact>]
let ``Used percent is clamped to 100`` () =
    let futureReset = DateTime.UtcNow.AddHours(1.0).ToString("o")
    let json = sprintf """{ "five_hour": %s }""" (bucket 150.0 futureReset)
    let r = parseJson json
    Assert.Equal(100.0, r.PrimaryUsed)

[<Fact>]
let ``Missing utilization field defaults to 0`` () =
    let json = """{ "five_hour": { "resets_at": "2030-01-01T00:00:00Z" } }"""
    let r = parseJson json
    Assert.Equal(0.0, r.PrimaryUsed)
    Assert.True(r.PrimaryReset <> "Never Resets")

[<Fact>]
let ``Empty resets_at string falls back to nominal window length`` () =
    let json = """{ "five_hour": { "utilization": 0.50, "resets_at": "" } }"""
    let r = parseJson json
    Assert.Equal(50.0, r.PrimaryUsed, 1)
    // The 5-hour window default shows "in 4h Xm" - the exact minute value
    // depends on test execution time. Match on the "in" prefix.
    Assert.StartsWith("in ", r.PrimaryReset)
    Assert.DoesNotContain("Never Resets", r.PrimaryReset)

[<Fact>]
let ``Missing resets_at field falls back to nominal window length`` () =
    let json = """{ "five_hour": { "utilization": 0.10 } }"""
    let r = parseJson json
    Assert.Equal(10.0, r.PrimaryUsed, 1)
    Assert.StartsWith("in ", r.PrimaryReset)
    Assert.DoesNotContain("Never Resets", r.PrimaryReset)

[<Fact>]
let ``Weekly bucket without resets_at falls back to 7d window`` () =
    let json = """{ "seven_day": { "utilization": 0.40 } }"""
    let r = parseJson json
    Assert.Equal(40.0, r.PrimaryUsed, 1)
    Assert.StartsWith("in ", r.PrimaryReset)
    Assert.Contains("d", r.PrimaryReset)

[<Fact>]
let ``Both buckets present: Session and Weekly fields both populated with raw values`` () =
    let futureReset = DateTime.UtcNow.AddHours(4.0).ToString("o")
    let laterReset = DateTime.UtcNow.AddDays(5.0).ToString("o")
    let json = sprintf """{ "five_hour": %s, "seven_day": %s }""" (bucket 0.45 futureReset) (bucket 0.20 laterReset)
    let r = parseJson json
    Assert.True(r.Session.IsSome, "Session bucket should be present")
    Assert.True(r.Weekly.IsSome, "Weekly bucket should be present")
    let s = r.Session |> Option.get
    let w = r.Weekly |> Option.get
    Assert.Equal(45.0, s.Used, 1)
    Assert.Equal(20.0, w.Used, 1)
    Assert.True(s.HasData)
    Assert.True(w.HasData)

[<Fact>]
let ``Only session bucket: Session populated, Weekly is None`` () =
    let futureReset = DateTime.UtcNow.AddHours(2.0).ToString("o")
    let json = sprintf """{ "five_hour": %s }""" (bucket 0.33 futureReset)
    let r = parseJson json
    Assert.True(r.Session.IsSome)
    Assert.True(r.Weekly.IsNone)

[<Fact>]
let ``Only weekly bucket: Weekly populated, Session is None`` () =
    let laterReset = DateTime.UtcNow.AddDays(3.0).ToString("o")
    let json = sprintf """{ "seven_day": %s }""" (bucket 0.66 laterReset)
    let r = parseJson json
    Assert.True(r.Session.IsNone)
    Assert.True(r.Weekly.IsSome)
    Assert.Equal(66.0, (r.Weekly |> Option.get).Used, 1)

[<Fact>]
let ``No buckets: both Session and Weekly are None`` () =
    let r = parseJson "{}"
    Assert.True(r.Session.IsNone)
    Assert.True(r.Weekly.IsNone)
