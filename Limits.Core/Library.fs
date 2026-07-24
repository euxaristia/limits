namespace Limits.Core

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

type UsageProvider =
    | Codex = 0
    | OpenAI = 1
    | Claude = 2
    | Cursor = 3
    | Gemini = 4
    | DeepSeek = 5
    | OpenRouter = 6
    | ElevenLabs = 7
    | Groq = 8
    | Bedrock = 9
    | Unknown = 10
    | Antigravity = 11

module ProviderMapping =
    let toString = function
        | UsageProvider.Codex -> "codex"
        | UsageProvider.OpenAI -> "openai"
        | UsageProvider.Claude -> "claude"
        | UsageProvider.Cursor -> "cursor"
        | UsageProvider.Gemini -> "gemini"
        | UsageProvider.DeepSeek -> "deepseek"
        | UsageProvider.OpenRouter -> "openrouter"
        | UsageProvider.ElevenLabs -> "elevenlabs"
        | UsageProvider.Groq -> "groq"
        | UsageProvider.Bedrock -> "bedrock"
        | UsageProvider.Antigravity -> "antigravity"
        | _ -> "unknown"

    let fromString = function
        | "codex" -> UsageProvider.Codex
        | "openai" -> UsageProvider.OpenAI
        | "claude" -> UsageProvider.Claude
        | "cursor" -> UsageProvider.Cursor
        | "gemini" -> UsageProvider.Gemini
        | "deepseek" -> UsageProvider.DeepSeek
        | "openrouter" -> UsageProvider.OpenRouter
        | "elevenlabs" -> UsageProvider.ElevenLabs
        | "groq" -> UsageProvider.Groq
        | "bedrock" -> UsageProvider.Bedrock
        | "antigravity" -> UsageProvider.Antigravity
        | _ -> UsageProvider.Unknown

    let getDisplayName = function
        | UsageProvider.Codex -> "Codex"
        | UsageProvider.OpenAI -> "OpenAI"
        | UsageProvider.Claude -> "Claude"
        | UsageProvider.Cursor -> "Cursor"
        | UsageProvider.Gemini -> "Gemini"
        | UsageProvider.DeepSeek -> "DeepSeek"
        | UsageProvider.OpenRouter -> "OpenRouter"
        | UsageProvider.ElevenLabs -> "ElevenLabs"
        | UsageProvider.Groq -> "Groq"
        | UsageProvider.Bedrock -> "AWS Bedrock"
        | UsageProvider.Antigravity -> "Antigravity"
        | _ -> "Unknown"

type ProviderConfig = {
    [<JsonPropertyName("id")>] id: string
    [<JsonPropertyName("enabled")>] enabled: Nullable<bool>
    [<JsonPropertyName("apiKey")>] apiKey: string
    [<JsonPropertyName("cookieHeader")>] cookieHeader: string
    [<JsonPropertyName("region")>] region: string
}

type LimitsConfig = {
    [<JsonPropertyName("version")>] version: int
    [<JsonPropertyName("providers")>] providers: ProviderConfig list
}

type CodexBarConfig = LimitsConfig

type UsageWindow = {
    /// Short label shown next to the progress bar (e.g. "Session", "Weekly", "Quota").
    Label: string
    /// Used as a percentage in [0, 100]. Always clamped before display.
    UsedPercent: float
    /// Human-readable reset countdown (e.g. "3h 42m", "1d 4h", "Never Resets").
    ResetCountdown: string
    /// Window length in seconds, or 0 if not applicable. Reserved for future
    /// features (pace, history) - not currently rendered in the popup.
    WindowSeconds: int
    /// Optional override of the percent text shown in the popup row. When
    /// None, the UI uses "{usedPercent}%". When set (e.g. "99.5% remaining"),
    /// it's displayed verbatim. Used by Antigravity to show the remaining
    /// percentage matching the agy CLI's display direction.
    PercentTextOverride: string option
}

type ProviderUsage = {
    Provider: UsageProvider
    Id: string
    DisplayName: string
    /// Ordered list of usage windows. Single-bucket providers emit exactly one
    /// window; multi-bucket providers (Claude, future Codex OAuth) emit two or
    /// more. The popup surfaces every window as its own row.
    Windows: UsageWindow list
    /// Status string: "healthy" | "degraded" | "unknown".
    Status: string
    IsMock: bool
    HasError: bool
    ErrorMessage: string
    /// Free-form footer line (replaces the old CostInfo slot). Carries the
    /// rich per-provider context that does not fit in a 0-100 bar (e.g.
    /// "Plan: Pro", "$4.82 of $18.00", "Spent: $0.85").
    Footer: string
}

module ConfigStore =
    let getDefaultConfigPath () =
        let envOverride = Environment.GetEnvironmentVariable("LIMITS_CONFIG")
        if not (String.IsNullOrWhiteSpace(envOverride)) then
            envOverride
        else
            let legacyOverride = Environment.GetEnvironmentVariable("CODEXBAR_CONFIG")
            if not (String.IsNullOrWhiteSpace(legacyOverride)) then
                legacyOverride
            else
                let xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let basePath =
                    if not (String.IsNullOrWhiteSpace(xdgConfig)) then
                        xdgConfig
                    else
                        Path.Combine(home, ".config")
                let limitsPath = Path.Combine(basePath, "limits", "config.json")
                if File.Exists(limitsPath) then
                    limitsPath
                else
                    let codexbarPath = Path.Combine(basePath, "codexbar", "config.json")
                    if File.Exists(codexbarPath) then codexbarPath else limitsPath

    let createDefaultConfig () : LimitsConfig =
        let defaultProviders = [
            { id = "codex"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "openai"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "claude"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "cursor"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "gemini"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "deepseek"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "openrouter"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "elevenlabs"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "antigravity"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
        ]
        { version = 1; providers = defaultProviders }

    let load () : LimitsConfig =
        let path = getDefaultConfigPath()
        try
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                JsonSerializer.Deserialize<LimitsConfig>(json, options)
            else
                let defaultConfig = createDefaultConfig()
                let dir = Path.GetDirectoryName(path)
                if not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore
                let json = JsonSerializer.Serialize(defaultConfig, JsonSerializerOptions(WriteIndented = true))
                File.WriteAllText(path, json)
                defaultConfig
        with _ ->
            createDefaultConfig()

    let save (config: LimitsConfig) =
        let path = getDefaultConfigPath()
        try
            let dir = Path.GetDirectoryName(path)
            if not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            let json = JsonSerializer.Serialize(config, JsonSerializerOptions(WriteIndented = true))
            File.WriteAllText(path, json)
        with _ ->
            ()

module AntigravityCredentials =
    // Reads the OAuth token that the Antigravity CLI / IDE store in the
    // Windows Credential Manager. The credential is a UTF-8 JSON blob
    // shaped like:
    //   { "token": { "access_token", "token_type", "refresh_token", "expiry" },
    //     "auth_method": "consumer" | ... }
    //
    // TargetName is `LegacyGeneric:target=gemini:antigravity` (verified via
    // `cmdkey /list`). UserName is `antigravity`. Persistence is local
    // machine, so the same creds are visible to every user-mode process.
    //
    // We P/Invoke advapi32!CredRead directly instead of taking a NuGet
    // dependency (e.g. Meziantou.Framework.Win32.CredentialManager) to
    // keep the dependency surface minimal. The DllImport shape mirrors
    // the Win32 CREDENTIAL struct (CRED_TYPE_GENERIC = 1).
    open System.Runtime.InteropServices

    [<Literal>]
    let private TargetName = "LegacyGeneric:target=gemini:antigravity"

    [<Literal>]
    let private CredTypeGeneric = 1u

    // Win32 interop declarations live in Win32.fs - F# struct syntax does
    // not allow [StructLayout] on a type with a primary constructor, and
    // member val fields require one. The CREDENTIAL struct and CredRead/
    // CredFree P/Invokes are declared there.

    exception AntigravityCredentialsError of string

    type Token = {
        AccessToken: string
        RefreshToken: string
        Expiry: DateTime option
        AuthMethod: string
    }

    let private parseTokenJson (json: string) : Token =
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let mutable tokenProp = new JsonElement()
        if not (root.TryGetProperty("token", &tokenProp)) || tokenProp.ValueKind <> JsonValueKind.Object then
            raise (AntigravityCredentialsError("Antigravity credential missing 'token' object"))
        let mutable accessProp = new JsonElement()
        if not (tokenProp.TryGetProperty("access_token", &accessProp)) || accessProp.ValueKind <> JsonValueKind.String then
            raise (AntigravityCredentialsError("Antigravity credential missing 'token.access_token' string"))
        let accessToken = accessProp.GetString()
        if String.IsNullOrEmpty(accessToken) then
            raise (AntigravityCredentialsError("Antigravity credential has empty access_token"))

        let refreshToken =
            let mutable p = new JsonElement()
            if tokenProp.TryGetProperty("refresh_token", &p) && p.ValueKind = JsonValueKind.String
            then p.GetString() else ""

        let expiry =
            let mutable p = new JsonElement()
            if tokenProp.TryGetProperty("expiry", &p) && p.ValueKind = JsonValueKind.String then
                let s = p.GetString()
                if not (String.IsNullOrEmpty(s)) then
                    match DateTime.TryParse(s) with
                    | true, d -> Some d
                    | _ -> None
                else None
            else None

        let authMethod =
            let mutable p = new JsonElement()
            if root.TryGetProperty("auth_method", &p) && p.ValueKind = JsonValueKind.String
            then p.GetString() else "unknown"

        {
            AccessToken = accessToken
            RefreshToken = refreshToken
            Expiry = expiry
            AuthMethod = authMethod
        }

    let load () : Token =
        if OperatingSystem.IsWindows() then
            let mutable credPtr = System.IntPtr.Zero
            let ok =
                try Win32.CredRead(TargetName, CredTypeGeneric, 0u, &credPtr)
                with ex ->
                    raise (AntigravityCredentialsError(sprintf "CredRead Win32 call threw: %s" ex.Message))
            if not ok then
                let err = Marshal.GetLastWin32Error()
                if err = 1168 || err = 1312 then
                    raise (AntigravityCredentialsError("No Antigravity credential found. Sign in via the Antigravity IDE or `agy` CLI."))
                else
                    raise (AntigravityCredentialsError(sprintf "CredRead failed with Win32 error %d" err))
            try
                let cred = Marshal.PtrToStructure(credPtr, typeof<CREDENTIAL>) :?> CREDENTIAL
                let size = int cred.CredentialBlobSize
                if size <= 0 || cred.CredentialBlob = System.IntPtr.Zero then
                    raise (AntigravityCredentialsError("Antigravity credential blob is empty"))
                let bytes : byte[] = Array.zeroCreate size
                Marshal.Copy(cred.CredentialBlob, bytes, 0, size)
                let json = System.Text.Encoding.UTF8.GetString(bytes)
                parseTokenJson json
            finally
                if credPtr <> System.IntPtr.Zero then
                    Win32.CredFree(credPtr)
        else
            let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let paths = [
                Path.Combine(home, ".gemini", "antigravity-cli", "credentials.json")
                Path.Combine(home, ".config", "antigravity", "credentials.json")
                Path.Combine(home, ".config", "agy", "credentials.json")
            ]
            match List.tryFind File.Exists paths with
            | Some path ->
                try
                    let json = File.ReadAllText(path)
                    parseTokenJson json
                with ex ->
                    raise (AntigravityCredentialsError(sprintf "Failed to parse Antigravity credential file at %s: %s" path ex.Message))
            | None ->
                raise (AntigravityCredentialsError("No Antigravity credential found on macOS/Linux. Sign in via `agy` CLI."))

module DateParser =
    let formatCountdown (resetsAtStr: string) =
        match DateTime.TryParse(resetsAtStr) with
        | true, date ->
            let diff = date.ToUniversalTime() - DateTime.UtcNow
            if diff.TotalSeconds <= 0.0 then
                "Resets now"
            else
                let hours = int diff.TotalHours
                let minutes = diff.Minutes
                if hours > 24 then
                    sprintf "%dd %dh" (hours / 24) (hours % 24)
                elif hours > 0 then
                    sprintf "%dh %dm" hours minutes
                else
                    sprintf "%dm" minutes
        | _ -> "Unknown"

module AntigravityUsageParser =
    // Parses the response of
    //   POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota
    // and groups buckets into shared (family, window) pairs.
    //
    // Response shape (verified against live agy credentials):
    //   { "buckets": [
    //     { "modelId": "gemini-2.5-pro",
    //       "remainingFraction": 0.9735987,
    //       "resetTime": "2026-06-30T11:56:23Z",
    //       "tokenType": "WTUS" },
    //     { "modelId": "claude-opus-4-6-thinking",
    //       "remainingFraction": 0,
    //       "resetTime": "2026-07-01T01:54:33Z" },
    //     { "modelId": "chat_23310",
    //       "remainingFraction": 1,    // no resetTime = placeholder model
    //       "tokenType": "WTUS" }
    //   ] }
    //
    // For your account this returns ~20 entries with 2-3 distinct resetTimes
    // (5h and weekly), and the API's "remainingFraction" is the right
    // metric to display on the bar (matching the agy CLI's "% remaining"
    // direction).
    //
    // Each modelId encodes its family (gemini-*, claude-*, gpt-oss-*) and
    // tier (low/medium/high/thinking/extra-low). We don't try to reverse
    // the API's internal tier naming back to user-facing labels - the
    // CLI does that via its "family" derivation in Swift. For the popup
    // we just group by family + reset time, dedupe duplicate modelIds,
    // and drop placeholder models (chat_*, tab_*).

    type Bucket = {
        /// Short family label derived from modelId: "Gemini", "Claude & GPT", etc.
        GroupLabel: string
        /// Comma-separated list of model IDs in the group.
        Members: string
        /// 0..100, 0 means "fully consumed" (limit hit).
        UsedPercent: float
        /// 0..100, what the CLI shows on the bar (remainingFraction * 100).
        RemainingPercent: float
        ResetCountdown: string
    }

    let empty = {
        GroupLabel = "Quota"
        Members = ""
        UsedPercent = 0.0
        RemainingPercent = 0.0
        ResetCountdown = "Never Resets"
    }

    let private familyFromModelId (modelId: string) : string =
        let m = (if String.IsNullOrEmpty modelId then "" else modelId).ToLowerInvariant()
        if m.StartsWith("gemini-") || m.Contains("gemini") then "Gemini"
        elif m.StartsWith("claude-") || m.Contains("claude") then "Claude & GPT"
        elif m.StartsWith("gpt-") || m.StartsWith("gpt_") then "Claude & GPT"
        elif m.StartsWith("chat_") || m.StartsWith("tab_") then "Internal"
        else "Antigravity"

    /// A single raw bucket entry from retrieveUserQuota.
    type private RawBucket = {
        ModelId: string
        Family: string
        RemainingFraction: float
        ResetCountdown: string
    }

    let private parseBucket (parent: JsonElement) : RawBucket option =
        let mutable idProp = new JsonElement()
        let modelId =
            if parent.TryGetProperty("modelId", &idProp) && idProp.ValueKind = JsonValueKind.String
            then idProp.GetString()
            else ""
        if String.IsNullOrEmpty modelId then None
        else
            let m = modelId.ToLowerInvariant()
            // Drop placeholder/internal models: chat_*, tab_*.
            if m.StartsWith("chat_") || m.StartsWith("tab_") then None
            else
                let mutable remProp = new JsonElement()
                let remaining =
                    if parent.TryGetProperty("remainingFraction", &remProp) && remProp.ValueKind = JsonValueKind.Number
                    then remProp.GetDouble()
                    else 0.0
                let mutable resetProp = new JsonElement()
                let reset =
                    if parent.TryGetProperty("resetTime", &resetProp) && resetProp.ValueKind = JsonValueKind.String
                    then
                        let s = resetProp.GetString()
                        if not (String.IsNullOrEmpty(s)) then DateParser.formatCountdown s else "Never Resets"
                    else "Never Resets"
                Some {
                    ModelId = modelId
                    Family = familyFromModelId modelId
                    RemainingFraction = remaining
                    ResetCountdown = reset
                }

    /// Parses the full retrieveUserQuota response into a list of grouped
    /// buckets. Drops placeholder models and entries with no modelId.
    /// Groups by (family, resetTime) - the API's resetTime field is the
    /// authoritative window length (5h vs weekly).
    let parse (root: JsonElement) : Bucket list =
        let mutable bucketsProp = new JsonElement()
        if not (root.TryGetProperty("buckets", &bucketsProp)) || bucketsProp.ValueKind <> JsonValueKind.Array then
            []
        else
            let raws =
                bucketsProp.EnumerateArray()
                |> Seq.choose parseBucket
                |> Seq.toList
            // Group by (family, resetCountdown)
            let groups = System.Collections.Generic.Dictionary<string, RawBucket list>()
            for entry in raws do
                let k = entry.Family + "|" + entry.ResetCountdown
                if groups.ContainsKey k then
                    let mutable existing : RawBucket list = Unchecked.defaultof<_>
                    if groups.TryGetValue(k, &existing) then
                        groups.[k] <- entry :: existing
                else
                    groups.[k] <- [entry]
            // Project each group to a Bucket
            let mutable result : Bucket list = []
            for kv in groups do
                let entries = kv.Value
                let first = List.head entries
                // Group's remainingFraction = the MIN of all members'
                // remainingFraction (most-pressed), since they share the
                // quota. The API's `retrieveUserQuota` returns the same
                // remainingFraction across all members of a window because
                // they share it, so min/max/avg are equivalent.
                let remaining =
                    entries
                    |> List.map (fun e -> e.RemainingFraction)
                    |> List.min
                let used = Math.Clamp((1.0 - remaining) * 100.0, 0.0, 100.0)
                let remainingPct = Math.Clamp(remaining * 100.0, 0.0, 100.0)
                let memberIds =
                    entries
                    |> List.map (fun e -> e.ModelId)
                    |> List.distinct
                    |> List.sort
                    |> String.concat ", "
                result <- {
                    GroupLabel = first.Family
                    Members = memberIds
                    UsedPercent = used
                    RemainingPercent = remainingPct
                    ResetCountdown = first.ResetCountdown
                } :: result
            let familyRank (label: string) =
                if label.Equals("Gemini", StringComparison.OrdinalIgnoreCase) then 0
                elif label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) then 1
                else 2

            result
            |> List.sortBy (fun b -> familyRank b.GroupLabel, b.ResetCountdown)

module ClaudeUsageParser =
    // Claude /usage returns two sibling buckets: a rolling 5-hour session
    // and a 7-day weekly quota. The bar can only surface a single primary
    // number, so we surface the more-pressed bucket as `Used` and pack both
    // values into `CostInfo` so neither is hidden.
    type Bucket = {
        HasData: bool
        Used: float
        ResetCountdown: string
    }

    let private empty = { HasData = false; Used = 0.0; ResetCountdown = "Never Resets" }

    let private normalizeUtilization (raw: float) : float =
        // API may return either a fraction (0..1) or a percentage (0..100+).
        if raw > 0.0 && raw <= 1.0 then raw * 100.0 else raw

    let private readBucket (parent: JsonElement) (name: string) (defaultWindowLabel: string) (defaultWindow: TimeSpan) : Bucket =
        let mutable prop = new JsonElement()
        if parent.TryGetProperty(name, &prop) && prop.ValueKind <> JsonValueKind.Null then
            let mutable util = new JsonElement()
            let mutable resets = new JsonElement()
            let mutable used = 0.0
            if prop.TryGetProperty("utilization", &util) && util.ValueKind <> JsonValueKind.Null then
                used <- normalizeUtilization (util.GetDouble())
            let mutable countdown = "Never Resets"
            if prop.TryGetProperty("resets_at", &resets) && resets.ValueKind <> JsonValueKind.Null then
                let s = resets.GetString()
                if not (String.IsNullOrEmpty(s)) then
                    let parsed = DateParser.formatCountdown s
                    // formatCountdown returns "Unknown" when DateTime.TryParse
                    // fails. In that case the server gave us something we
                    // can't interpret - fall back to the window's nominal
                    // length rather than showing the user a perpetually-
                    // broken reset.
                    if parsed <> "Unknown" then
                        countdown <- parsed
                    else
                        countdown <- sprintf "in %s" (DateParser.formatCountdown (DateTime.UtcNow.Add(defaultWindow).ToString("o")))
                else
                    // Empty resets_at string - fall back to the window's
                    // nominal length.
                    countdown <- sprintf "in %s" (DateParser.formatCountdown (DateTime.UtcNow.Add(defaultWindow).ToString("o")))
            else
                // No resets_at field at all (e.g. bucket has 0% used and the
                // server doesn't bother computing a reset). Show the nominal
                // window length so the user sees something useful.
                countdown <- sprintf "in %s" (DateParser.formatCountdown (DateTime.UtcNow.Add(defaultWindow).ToString("o")))
            { HasData = true; Used = used; ResetCountdown = countdown }
        else
            empty

    type ParseResult = {
        PrimaryUsed: float
        PrimaryReset: string
        PrimaryLabel: string
        CostInfo: string
        /// Per-bucket breakdown. Always length 0, 1, or 2; values are
        /// pre-normalized to [0, 100] percentages.
        Session: Bucket option
        Weekly: Bucket option
    }

    /// Picks the more-pressed bucket as primary. On ties the weekly quota wins
    /// (lower per-hour burn rate -> the more informative "watch this one").
    let parse (root: JsonElement) : ParseResult =
        let session = readBucket root "five_hour" "5h" (TimeSpan.FromHours 5.0)
        let weekly = readBucket root "seven_day" "7d" (TimeSpan.FromDays 7.0)

        let sessionPct = if session.HasData then Math.Round(session.Used, 1) else Double.NaN
        let weeklyPct = if weekly.HasData then Math.Round(weekly.Used, 1) else Double.NaN

        let primaryPct, primaryReset, primaryLabel =
            match session.HasData, weekly.HasData with
            | false, false ->
                0.0, "Never Resets", "Claude Plan"
            | true, false ->
                session.Used, session.ResetCountdown, "5-hour session quota"
            | false, true ->
                weekly.Used, weekly.ResetCountdown, "7-day weekly quota"
            | true, true ->
                if weekly.Used >= session.Used then
                    weekly.Used, weekly.ResetCountdown, "7-day weekly quota"
                else
                    session.Used, session.ResetCountdown, "5-hour session quota"

        let costInfo =
            match session.HasData, weekly.HasData with
            | true, true ->
                sprintf "Session: %g%% \u00B7 7-day: %g%%" sessionPct weeklyPct
            | _ ->
                primaryLabel

        {
            PrimaryUsed = Math.Clamp(primaryPct, 0.0, 100.0)
            PrimaryReset = primaryReset
            PrimaryLabel = primaryLabel
            CostInfo = costInfo
            Session = if session.HasData then Some session else None
            Weekly = if weekly.HasData then Some weekly else None
        }

module UsageFetcher =
    let private client = new HttpClient()

    // Set modern User-Agent to avoid getting blocked by Cloudflare/WAF
    let private setupHeaders (headers: HttpRequestHeaders) =
        headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
        headers.Accept.ParseAdd("application/json, text/plain, */*")

    /// Build a single-window ProviderUsage record. Most providers only have
    /// one quota, so this centralizes the "wrap into a list" step.
    let private singleWindow
        (provider: UsageProvider)
        (id: string)
        (displayName: string)
        (used: float)
        (limit: float)
        (resetCountdown: string)
        (status: string)
        (isMock: bool)
        (hasError: bool)
        (errorMessage: string)
        (footer: string)
        : ProviderUsage =
        let usedPct =
            if limit > 0.0
            then Math.Clamp(used / limit * 100.0, 0.0, 100.0)
            else 0.0
        {
            Provider = provider
            Id = id
            DisplayName = displayName
            Windows = [{
                Label = "Quota"
                UsedPercent = usedPct
                ResetCountdown = resetCountdown
                WindowSeconds = 0
                PercentTextOverride = None
            }]
            Status = status
            IsMock = isMock
            HasError = hasError
            ErrorMessage = errorMessage
            Footer = footer
        }

    /// Build a multi-window ProviderUsage record from raw percentage values.
    /// Each tuple is (label, usedPercent 0-100, resetCountdown, windowSeconds).
    let private multiWindow
        (provider: UsageProvider)
        (id: string)
        (displayName: string)
        (windows: (string * float * string * int * string option) list)
        (status: string)
        (isMock: bool)
        (hasError: bool)
        (errorMessage: string)
        (footer: string)
        : ProviderUsage =
        {
            Provider = provider
            Id = id
            DisplayName = displayName
            Windows =
                windows
                |> List.map (fun (label, pct, reset, secs, pctOverride) ->
                    { Label = label
                      UsedPercent = Math.Clamp(pct, 0.0, 100.0)
                      ResetCountdown = reset
                      WindowSeconds = secs
                      PercentTextOverride = pctOverride })
            Status = status
            IsMock = isMock
            HasError = hasError
            ErrorMessage = errorMessage
            Footer = footer
        }

    // 1. OpenAI Credit Grants API
    let private fetchOpenAIBalance (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.OpenAI
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/dashboard/billing/credit_grants")
            setupHeaders request.Headers
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey.Trim())

            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let root = doc.RootElement
                let totalGranted = root.GetProperty("total_granted").GetDouble()
                let totalUsed = root.GetProperty("total_used").GetDouble()
                let totalAvailable = root.GetProperty("total_available").GetDouble()

                return singleWindow
                    provider "openai" name
                    totalUsed totalGranted
                    "Credit Expiry" "healthy" false false ""
                    (sprintf "Available: $%M / $%M" (decimal totalAvailable) (decimal totalGranted))
            else
                return singleWindow
                    provider "openai" name
                    0.0 100.0 "N/A" "degraded" false true
                    (sprintf "API returned status %d" (int response.StatusCode))
                    ""
        with ex ->
            return singleWindow
                provider "openai" name
                0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    // 2. Claude Web API (using browser session cookies)
    let private fetchClaudeUsage (cookieSource: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.Claude
        let name = ProviderMapping.getDisplayName provider
        try
            // Extract sessionKey from cookie header
            let sessionKey =
                if cookieSource.Contains("sessionKey=") then
                    let start = cookieSource.IndexOf("sessionKey=") + "sessionKey=".Length
                    let endIdx = cookieSource.IndexOf(";", start)
                    if endIdx = -1 then cookieSource.Substring(start).Trim()
                    else cookieSource.Substring(start, endIdx - start).Trim()
                else
                    cookieSource.Trim()

            // Step A: Fetch organizations
            use orgRequest = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations")
            setupHeaders orgRequest.Headers
            orgRequest.Headers.Add("Cookie", sprintf "sessionKey=%s" sessionKey)

            let! orgResponse = client.SendAsync(orgRequest)
            if orgResponse.IsSuccessStatusCode then
                let! orgContent = orgResponse.Content.ReadAsStringAsync()
                use orgDoc = JsonDocument.Parse(orgContent)
                let orgsArray = orgDoc.RootElement
                if orgsArray.ValueKind = JsonValueKind.Array && orgsArray.GetArrayLength() > 0 then
                    let orgId = orgsArray.[0].GetProperty("uuid").GetString()

                    // Step B: Fetch usage for the organization
                    let usageUrl = sprintf "https://claude.ai/api/organizations/%s/usage" orgId
                    use usageRequest = new HttpRequestMessage(HttpMethod.Get, usageUrl)
                    setupHeaders usageRequest.Headers
                    usageRequest.Headers.Add("Cookie", sprintf "sessionKey=%s" sessionKey)

                    let! usageResponse = client.SendAsync(usageRequest)
                    if usageResponse.IsSuccessStatusCode then
                        let! usageContent = usageResponse.Content.ReadAsStringAsync()
                        use usageDoc = JsonDocument.Parse(usageContent)
                        let root = usageDoc.RootElement

                        let parsed = ClaudeUsageParser.parse root

                        // Build the windows list: Session first, Weekly second (or just the one we have).
                        let mutable winTuples: (string * float * string * int * string option) list = []
                        match parsed.Session with
                        | Some s -> winTuples <- ("Session", s.Used, s.ResetCountdown, 5 * 3600, None) :: winTuples
                        | None -> ()
                        match parsed.Weekly with
                        | Some w -> winTuples <- ("Weekly", w.Used, w.ResetCountdown, 7 * 24 * 3600, None) :: winTuples
                        | None -> ()
                        let windows = List.rev winTuples

                        return multiWindow
                            provider "claude" name windows
                            "healthy" false false "" parsed.CostInfo
                    else
                        return singleWindow
                            provider "claude" name
                            0.0 100.0 "N/A" "degraded" false true
                            (sprintf "Usage API returned HTTP %d" (int usageResponse.StatusCode))
                            ""
                else
                    return singleWindow
                        provider "claude" name
                        0.0 100.0 "N/A" "degraded" false true
                        "No organization UUID found" ""
            else
                return singleWindow
                    provider "claude" name
                    0.0 100.0 "N/A" "degraded" false true
                    (sprintf "Orgs API returned HTTP %d (Invalid sessionKey?)" (int orgResponse.StatusCode))
                    ""
        with ex ->
            return singleWindow
                provider "claude" name
                0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    // 3. DeepSeek User Balance API
    let private fetchDeepSeekBalance (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.DeepSeek
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance")
            setupHeaders request.Headers
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey.Trim())

            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let root = doc.RootElement
                if root.GetProperty("is_available").GetBoolean() then
                    let balanceInfos = root.GetProperty("balance_infos")
                    let mutable totalBalance = 0.0
                    for balanceInfo in balanceInfos.EnumerateArray() do
                        let balanceStr = balanceInfo.GetProperty("balance").GetString()
                        let balance = Double.Parse(balanceStr)
                        totalBalance <- totalBalance + balance

                    return singleWindow
                        provider "deepseek" name
                        0.0 totalBalance "Never Resets" "healthy" false false ""
                        (sprintf "Available: $%M" (decimal totalBalance))
                else
                    return singleWindow
                        provider "deepseek" name
                        0.0 0.0 "N/A" "degraded" false true "Account is unavailable" ""
            else
                return singleWindow
                    provider "deepseek" name
                    0.0 0.0 "N/A" "degraded" false true
                    (sprintf "API returned status %d" (int response.StatusCode)) ""
        with ex ->
            return singleWindow
                provider "deepseek" name
                0.0 0.0 "N/A" "degraded" false true ex.Message ""
    }

    // 4. OpenRouter Key Info API
    let private fetchOpenRouterBalance (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.OpenRouter
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key")
            setupHeaders request.Headers
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey.Trim())

            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let root = doc.RootElement
                let data = root.GetProperty("data")
                let limit = data.GetProperty("limit").GetDouble()
                let usage = data.GetProperty("usage").GetDouble()

                return singleWindow
                    provider "openrouter" name
                    usage limit "Monthly Reset" "healthy" false false ""
                    (sprintf "Used: $%M / $%M" (decimal usage) (decimal limit))
            else
                return singleWindow
                    provider "openrouter" name
                    0.0 0.0 "N/A" "degraded" false true
                    (sprintf "API returned status %O" response.StatusCode) ""
        with ex ->
            return singleWindow
                provider "openrouter" name
                0.0 0.0 "N/A" "degraded" false true ex.Message ""
    }

    // 5. Gemini Private Quota API (loads credentials from local gcloud/gemini-cli workspace)
    let rec private fetchGeminiUsage () : Task<ProviderUsage> = task {
        let provider = UsageProvider.Gemini
        let name = ProviderMapping.getDisplayName provider
        try
            let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let credsPath = Path.Combine(userProfile, ".gemini", "oauth_creds.json")

            if File.Exists(credsPath) then
                let credsJson = File.ReadAllText(credsPath)
                use doc = JsonDocument.Parse(credsJson)
                let root = doc.RootElement

                let mutable accessTokenProp = new JsonElement()
                if root.TryGetProperty("access_token", &accessTokenProp) then
                    let accessToken = accessTokenProp.GetString()

                    // Call the retrieveUserQuota RPC endpoint
                    use request = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")
                    setupHeaders request.Headers
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
                    request.Content <- new StringContent("{}", System.Text.Encoding.UTF8, "application/json")

                    let! response = client.SendAsync(request)
                    if response.IsSuccessStatusCode then
                        let! content = response.Content.ReadAsStringAsync()
                        use quotaDoc = JsonDocument.Parse(content)
                        let quotaRoot = quotaDoc.RootElement

                        let mutable lowestFraction = 1.0
                        let mutable resetTime = "Daily Quota"

                        let mutable bucketsProp = new JsonElement()
                        let mutable fractionProp = new JsonElement()
                        let mutable resetProp = new JsonElement()

                        if quotaRoot.TryGetProperty("buckets", &bucketsProp) && bucketsProp.ValueKind = JsonValueKind.Array then
                            for bucket in bucketsProp.EnumerateArray() do
                                if bucket.TryGetProperty("remainingFraction", &fractionProp) then
                                    let fraction = fractionProp.GetDouble()
                                    if fraction < lowestFraction then
                                        lowestFraction <- fraction
                                if bucket.TryGetProperty("resetTime", &resetProp) then
                                    resetTime <- DateParser.formatCountdown (resetProp.GetString())

                        let usedPercent = (1.0 - lowestFraction) * 100.0
                        return singleWindow
                            provider "gemini" name
                            usedPercent 100.0 resetTime "healthy" false false ""
                            "Google Code Assist Quota"
                    else
                        return singleWindow
                            provider "gemini" name
                            0.0 100.0 "N/A" "degraded" false true
                            (sprintf "Quota API returned status %d" (int response.StatusCode))
                            ""
                else
                    return singleWindow
                        provider "gemini" name
                        0.0 100.0 "N/A" "degraded" false true
                        "Credentials file missing access_token" ""
            else
                // No credentials file found - generate mock telemetry
                return getMockData provider
        with ex ->
            return singleWindow
                provider "gemini" name
                0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    // 6. Antigravity (Google Cloud Code Assist) - reads OAuth from Windows
    // Credential Manager and calls v1internal:retrieveUserQuota, the same
    // endpoint the existing Gemini fetcher uses. The response carries
    // per-model bucket data with resetTimes, so we can group by
    // (family, window) and surface 2 bars per family (5h + weekly)
    // matching the agy CLI's display.
    and private fetchAntigravityUsage () : Task<ProviderUsage> = task {
        let provider = UsageProvider.Antigravity
        let name = ProviderMapping.getDisplayName provider
        try
            let token = AntigravityCredentials.load ()

            use request = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")
            setupHeaders request.Headers
            // The Swift reference uses User-Agent: antigravity for these calls.
            // setupHeaders already set a Chrome User-Agent; replace it (not
            // append - duplicate User-Agent headers cause some clouds to 400).
            request.Headers.UserAgent.Clear()
            request.Headers.UserAgent.ParseAdd("antigravity")
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.AccessToken)
            // Empty body - the Swift code includes {"project": <id>} when
            // known, but for consumer accounts (the common case) empty works
            // and loadCodeAssist is not needed.
            request.Content <- new StringContent("{}", System.Text.Encoding.UTF8, "application/json")

            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let buckets = AntigravityUsageParser.parse doc.RootElement

                if List.isEmpty buckets then
                    // Endpoint reachable but every model was filtered or
                    // had no quotaInfo. Could be an account with no models
                    // provisioned yet.
                    return singleWindow
                        provider "antigravity" name
                        0.0 100.0 "N/A" "healthy" false false ""
                        (sprintf "Antigravity (%s) - no quota data" token.AuthMethod)
                else
                    // One row per (family, reset-window) group. The bar
                    // shows remaining%, matching the agy CLI's display.
                    let windows =
                        buckets
                        |> List.map (fun b ->
                            let label =
                                sprintf "%s \u2022 %d model%s"
                                    b.GroupLabel
                                    (b.Members.Split ',').Length
                                    (if (b.Members.Split ',').Length = 1 then "" else "s")
                            // 1-decimal precision when remaining% is small so
                            // 0.2% remaining doesn't render as 0% (looks like
                            // the limit is hit when it isn't). The CLI shows
                            // integer percentages; we use 1 decimal to
                            // disambiguate "fully used" from "barely used".
                            let remainingText =
                                if b.RemainingPercent < 10.0
                                then sprintf "%.1f%% remaining" b.RemainingPercent
                                else sprintf "%d%% remaining" (int (b.RemainingPercent + 0.5))
                            (label, b.UsedPercent, b.ResetCountdown, 0, Some remainingText))
                    let modelCount = buckets |> List.sumBy (fun b -> (b.Members.Split ',').Length)
                    let footer =
                        sprintf "Antigravity (%s) - %d group%s, %d model%s"
                            token.AuthMethod
                            (List.length buckets)
                            (if List.length buckets = 1 then "" else "s")
                            modelCount
                            (if modelCount = 1 then "" else "s")
                    return multiWindow
                        provider "antigravity" name windows
                        "healthy" false false "" footer
            else
                // Surface the actual error so the user can see why the
                // call failed (e.g. 400 from duplicate User-Agent, 403 from
                // permission denied). The next refactor will turn these
                // into typed ProviderUsage errors.
                let! errContent = response.Content.ReadAsStringAsync()
                let errMsg = sprintf "Antigravity API returned status %d: %s" (int response.StatusCode) (if errContent.Length > 200 then errContent.Substring(0, 200) + "..." else errContent)
                return singleWindow
                    provider "antigravity" name
                    0.0 100.0 "N/A" "degraded" false true errMsg ""
        with
        | :? AntigravityCredentials.AntigravityCredentialsError as credErr ->
            return singleWindow
                provider "antigravity" name
                0.0 100.0 "N/A" "degraded" false true credErr.Message ""
        | ex ->
            return singleWindow
                provider "antigravity" name
                0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    // Generates high-fidelity mock data if api key is missing
    and getMockData (provider: UsageProvider) : ProviderUsage =
        let name = ProviderMapping.getDisplayName provider
        let id = ProviderMapping.toString provider
        match provider with
        | UsageProvider.Codex ->
            singleWindow provider id name
                142.0 500.0 "1h 42m" "healthy" true false ""
                "Plan: Pro"
        | UsageProvider.OpenAI ->
            singleWindow provider id name
                4.82 18.0 "Resets in 2d" "healthy" true false ""
                "Spent: $4.82 of $18.00"
        | UsageProvider.Claude ->
            // Mock Claude: surface a Session + Weekly split so the multi-window
            // UI has something realistic to render for unmocked-Claude users.
            multiWindow provider id name
                [("Session", 56.0, "4h 12m", 5 * 3600, None);
                 ("Weekly", 28.0, "5d 6h", 7 * 24 * 3600, None)]
                "healthy" true false "" "Plan: Claude Pro"
        | UsageProvider.Cursor ->
            singleWindow provider id name
                384.0 500.0 "Resets July 5" "healthy" true false ""
                "384 / 500 fast requests"
        | UsageProvider.Gemini ->
            singleWindow provider id name
                60.0 100.0 "Daily Quota" "healthy" true false ""
                "RPM: 15 / 360"
        | UsageProvider.DeepSeek ->
            singleWindow provider id name
                2.15 15.0 "Never Resets" "healthy" true false ""
                "Balance: $12.85 available"
        | UsageProvider.OpenRouter ->
            singleWindow provider id name
                0.85 5.0 "Monthly Reset" "healthy" true false ""
                "Spent: $0.85"
        | UsageProvider.ElevenLabs ->
            singleWindow provider id name
                42500.0 100000.0 "Resets in 12d" "healthy" true false ""
                "42,500 characters used"
        | UsageProvider.Groq ->
            singleWindow provider id name
                65.0 100.0 "Minute Limit" "healthy" true false ""
                "65 / 100 requests"
        | UsageProvider.Bedrock ->
            singleWindow provider id name
                12.45 50.0 "Billing Cycle" "healthy" true false ""
                "Cost this month: $12.45"
        | _ ->
            singleWindow provider id name
                0.0 100.0 "Unknown" "unknown" true false "" ""

    // 2b. Claude OAuth API (from local Claude CLI credentials)
    let private fetchClaudeOAuthUsage (accessToken: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.Claude
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage")
            setupHeaders request.Headers
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken.Trim())
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20")

            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let root = doc.RootElement

                let parsed = ClaudeUsageParser.parse root

                let mutable winTuples: (string * float * string * int * string option) list = []
                match parsed.Session with
                | Some s -> winTuples <- ("Session", s.Used, s.ResetCountdown, 5 * 3600, None) :: winTuples
                | None -> ()
                match parsed.Weekly with
                | Some w -> winTuples <- ("Weekly", w.Used, w.ResetCountdown, 7 * 24 * 3600, None) :: winTuples
                | None -> ()
                let windows = List.rev winTuples

                return multiWindow
                    provider "claude" name windows
                    "healthy" false false "" parsed.CostInfo
            else
                return singleWindow
                    provider "claude" name
                    0.0 100.0 "N/A" "degraded" false true
                    (sprintf "OAuth API returned HTTP %d" (int response.StatusCode))
                    ""
        with ex ->
            return singleWindow
                provider "claude" name
                0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    let fetch (config: ProviderConfig) : Task<ProviderUsage> = task {
        let provider = ProviderMapping.fromString config.id

        // Check keys/cookies
        let hasApiKey = not (String.IsNullOrWhiteSpace(config.apiKey))
        let hasCookie = not (String.IsNullOrWhiteSpace(config.cookieHeader))

        match provider with
        | UsageProvider.OpenAI when hasApiKey ->
            return! fetchOpenAIBalance config.apiKey
        | UsageProvider.Claude ->
            if hasCookie || hasApiKey then
                let token = if hasCookie then config.cookieHeader else config.apiKey
                return! fetchClaudeUsage token
            else
                // Auto-fallback: try reading ~/.claude/.credentials.json
                let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let credsPath = Path.Combine(userProfile, ".claude", ".credentials.json")
                if File.Exists(credsPath) then
                    try
                        let json = File.ReadAllText(credsPath)
                        use doc = JsonDocument.Parse(json)
                        let root = doc.RootElement
                        let mutable oauthProp = new JsonElement()
                        let mutable tokenProp = new JsonElement()
                        if root.TryGetProperty("claudeAiOauth", &oauthProp) && oauthProp.TryGetProperty("accessToken", &tokenProp) then
                            let token = tokenProp.GetString()
                            if not (String.IsNullOrWhiteSpace(token)) then
                                return! fetchClaudeOAuthUsage token
                            else
                                return getMockData provider
                        else
                            return getMockData provider
                    with _ ->
                        return getMockData provider
                else
                    return getMockData provider
        | UsageProvider.DeepSeek when hasApiKey ->
            return! fetchDeepSeekBalance config.apiKey
        | UsageProvider.OpenRouter when hasApiKey ->
            return! fetchOpenRouterBalance config.apiKey
        | UsageProvider.Gemini ->
            return! fetchGeminiUsage ()
        | UsageProvider.Antigravity ->
            return! fetchAntigravityUsage ()
        | _ ->
            return getMockData provider
    }
