module Limits.Core.Tests.CliOAuthRefresherTests

open System
open Xunit
open Limits.Core

[<Fact>]
let ``GrokCredentials.isWorking returns true for unexpired token`` () =
    let token: GrokCredentials.Token = {
        AccessToken = "valid-token-123"
        Email = "user@example.com"
        ExpiresAt = Some (DateTime.UtcNow.AddMinutes(45.0))
        AuthMode = "oidc"
    }
    Assert.True(GrokCredentials.isWorking (Some token))

[<Fact>]
let ``GrokCredentials.isWorking returns false for expired token`` () =
    let token: GrokCredentials.Token = {
        AccessToken = "expired-token-123"
        Email = "user@example.com"
        ExpiresAt = Some (DateTime.UtcNow.AddMinutes(-5.0))
        AuthMode = "oidc"
    }
    Assert.False(GrokCredentials.isWorking (Some token))

[<Fact>]
let ``GrokCredentials.isWorking returns false for None or empty token`` () =
    Assert.False(GrokCredentials.isWorking None)

    let emptyToken: GrokCredentials.Token = {
        AccessToken = "   "
        Email = ""
        ExpiresAt = Some (DateTime.UtcNow.AddMinutes(45.0))
        AuthMode = "oidc"
    }
    Assert.False(GrokCredentials.isWorking (Some emptyToken))

[<Fact>]
let ``CliOAuthRefresher.isCliAvailable returns false for non-existent CLI`` () =
    Assert.False(CliOAuthRefresher.isCliAvailable "non_existent_tool_xyz_999")
    Assert.False(CliOAuthRefresher.isCliAvailable "")

[<Fact>]
let ``CliOAuthRefresher.getMatchingCliCandidates returns correct candidates for providers`` () =
    let grokCandidates = CliOAuthRefresher.getMatchingCliCandidates UsageProvider.Grok
    Assert.NotEmpty(grokCandidates)
    Assert.Contains(grokCandidates, fun (cmd, _) -> cmd = "grok")

    let antigravityCandidates = CliOAuthRefresher.getMatchingCliCandidates UsageProvider.Antigravity
    Assert.NotEmpty(antigravityCandidates)
    Assert.Contains(antigravityCandidates, fun (cmd, _) -> cmd = "agy" || cmd = "antigravity")

    let claudeCandidates = CliOAuthRefresher.getMatchingCliCandidates UsageProvider.Claude
    Assert.NotEmpty(claudeCandidates)
    Assert.Contains(claudeCandidates, fun (cmd, _) -> cmd = "claude")

[<Fact>]
let ``CliOAuthRefresher.ensureTokenReady returns true for non-OAuth provider without running CLI`` () =
    let task = CliOAuthRefresher.ensureTokenReady UsageProvider.OpenAI
    let ready = task.GetAwaiter().GetResult()
    Assert.True(ready)
