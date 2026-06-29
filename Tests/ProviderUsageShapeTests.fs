module CodexBarWin.Core.Tests.ProviderUsageShapeTests

open Xunit
open CodexBarWin.Core

/// Asserts the ProviderUsage / UsageWindow data model contract.
/// Most of the runtime fetchers are HttpClient wrappers we cannot exercise
/// in unit tests, but their shared contract is the only thing the UI binds
/// to, so it deserves direct coverage.
module Contract =
    [<Fact>]
    let ``ProviderUsage record exposes Windows: UsageWindow list`` () =
        // We can construct it directly; the compiler enforces the shape.
        let usage: ProviderUsage = {
            Provider = UsageProvider.OpenAI
            Id = "openai"
            DisplayName = "OpenAI"
            Windows = [{
                Label = "Quota"
                UsedPercent = 27.0
                ResetCountdown = "Resets in 2d"
                WindowSeconds = 0
            }]
            Status = "healthy"
            IsMock = false
            HasError = false
            ErrorMessage = ""
            Footer = "Spent: $4.82 of $18.00"
        }
        Assert.Equal(1, List.length usage.Windows)
        Assert.Equal("Quota", (List.head usage.Windows).Label)

    [<Fact>]
    let ``ProviderUsage can carry multiple Windows (Claude-style two-bucket shape)`` () =
        let usage: ProviderUsage = {
            Provider = UsageProvider.Claude
            Id = "claude"
            DisplayName = "Claude"
            Windows = [
                { Label = "Session"; UsedPercent = 2.0; ResetCountdown = "3h 53m"; WindowSeconds = 5 * 3600 }
                { Label = "Weekly"; UsedPercent = 3.0; ResetCountdown = "3d 20h"; WindowSeconds = 7 * 24 * 3600 }
            ]
            Status = "healthy"
            IsMock = false
            HasError = false
            ErrorMessage = ""
            Footer = "Session: 2% \u00B7 7-day: 3%"
        }
        Assert.Equal(2, List.length usage.Windows)
        let labels = usage.Windows |> List.map (fun w -> w.Label)
        Assert.Equal<string list>(["Session"; "Weekly"], labels)

    [<Fact>]
    let ``UsageWindow fields round-trip through a record literal`` () =
        let w: UsageWindow = {
            Label = "Session"
            UsedPercent = 56.7
            ResetCountdown = "4h 12m"
            WindowSeconds = 18000
        }
        Assert.Equal("Session", w.Label)
        Assert.Equal(56.7, w.UsedPercent)
        Assert.Equal("4h 12m", w.ResetCountdown)
        Assert.Equal(18000, w.WindowSeconds)

/// WindowSeconds is a hint for future features (pace, history); current
/// fetchers always emit 0 for single-bucket providers. Multi-bucket
/// providers (Claude) should set it to the actual window length.
module WindowSecondsContract =
    [<Fact>]
    let ``Single-bucket provider window carries WindowSeconds = 0`` () =
        let w: UsageWindow = { Label = "Quota"; UsedPercent = 50.0; ResetCountdown = "1h"; WindowSeconds = 0 }
        Assert.Equal(0, w.WindowSeconds)

    [<Fact>]
    let ``Multi-bucket provider windows carry real window lengths`` () =
        let session: UsageWindow = { Label = "Session"; UsedPercent = 50.0; ResetCountdown = "1h"; WindowSeconds = 5 * 3600 }
        let weekly: UsageWindow = { Label = "Weekly"; UsedPercent = 50.0; ResetCountdown = "1d"; WindowSeconds = 7 * 24 * 3600 }
        Assert.Equal(18000, session.WindowSeconds)
        Assert.Equal(604800, weekly.WindowSeconds)
