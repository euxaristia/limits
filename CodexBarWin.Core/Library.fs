namespace CodexBarWin.Core

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

type CodexBarConfig = {
    [<JsonPropertyName("version")>] version: int
    [<JsonPropertyName("providers")>] providers: ProviderConfig list
}

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
        let envOverride = Environment.GetEnvironmentVariable("CODEXBAR_CONFIG")
        if not (String.IsNullOrWhiteSpace(envOverride)) then
            envOverride
        else
            let xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let basePath =
                if not (String.IsNullOrWhiteSpace(xdgConfig)) then
                    xdgConfig
                else
                    Path.Combine(home, ".config")
            Path.Combine(basePath, "codexbar", "config.json")

    let createDefaultConfig () : CodexBarConfig =
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

    let load () : CodexBarConfig =
        let path = getDefaultConfigPath()
        try
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                JsonSerializer.Deserialize<CodexBarConfig>(json, options)
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

    let save (config: CodexBarConfig) =
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

    let load () : Token =
        let mutable credPtr = System.IntPtr.Zero
        let ok =
            try Win32.CredRead(TargetName, CredTypeGeneric, 0u, &credPtr)
            with ex ->
                raise (AntigravityCredentialsError(sprintf "CredRead Win32 call threw: %s" ex.Message))
        if not ok then
            let err = Marshal.GetLastWin32Error()
            // 1168 = ERROR_NOT_FOUND, 1312 = ERROR_NO_SUCH_LOGON_SESSION. Both mean
            // "no credential stored" from the caller's perspective.
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
        finally
            Win32.CredFree(credPtr)

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
    //   POST https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels
    // and groups models into shared quota windows.
    //
    // Response shape:
    //   { "models": { "<modelId>": {
    //       "displayName": "Gemini 2.5 Pro",
    //       "label": "...",
    //       "modelProvider": "MODEL_PROVIDER_GOOGLE" | "MODEL_PROVIDER_ANTHROPIC" | ...,
    //       "quotaInfo": { "remainingFraction"?: number, "resetTime"?: string }
    //   } } }
    //
    // Two filters: drop internal placeholder models (chat_*, tab_*,
    // MODEL_PLACEHOLDER_*, no displayName) and drop entries with no
    // quotaInfo. Then group by (modelProvider, resetTime) - models in the
    // same group share both - and produce one Bucket per group.
    //
    // Bar value is the group's remainingFraction (or 0 if missing, which
    // means the window is hit - the CLI shows this as 0.00% with a red
    // bar and a "limit hit" message). This matches what the `agy models`
    // CLI displays.

    type Bucket = {
        /// Short family label derived from modelProvider: "Gemini", "Claude & GPT", etc.
        GroupLabel: string
        /// First displayName in the group, used as the row label fallback.
        PrimaryModel: string
        /// Comma-separated list of display names in the group.
        Members: string
        /// 0..100, 0 means "fully consumed" (limit hit or no fraction in response).
        UsedPercent: float
        /// 0..100, what the CLI shows on the bar (remainingFraction * 100).
        RemainingPercent: float
        ResetCountdown: string
    }

    let empty = {
        GroupLabel = "Quota"
        PrimaryModel = ""
        Members = ""
        UsedPercent = 0.0
        RemainingPercent = 0.0
        ResetCountdown = "Never Resets"
    }

    let private getStringField (parent: JsonElement) (name: string) : string =
        let mutable p = new JsonElement()
        if parent.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.String
        then p.GetString()
        else ""

    let private getDisplayName (parent: JsonElement) (modelId: string) : string =
        let dn = getStringField parent "displayName"
        if not (String.IsNullOrEmpty dn) then dn
        else
            let lbl = getStringField parent "label"
            if not (String.IsNullOrEmpty lbl) then lbl
            else modelId

    let private providerToGroup (provider: string) : string =
        match provider with
        | "MODEL_PROVIDER_GOOGLE" -> "Gemini"
        | "MODEL_PROVIDER_ANTHROPIC" -> "Claude & GPT"
        | "MODEL_PROVIDER_OPENAI" -> "Claude & GPT"
        | p when not (String.IsNullOrEmpty p) -> p.Replace("MODEL_PROVIDER_", "")
        | _ -> "Antigravity"

    let private parseQuotaInfo (parent: JsonElement) : (float option * string) option =
        let mutable qiProp = new JsonElement()
        if not (parent.TryGetProperty("quotaInfo", &qiProp)) || qiProp.ValueKind <> JsonValueKind.Object then
            None
        else
            let mutable remainingProp = new JsonElement()
            let remaining =
                if qiProp.TryGetProperty("remainingFraction", &remainingProp) && remainingProp.ValueKind = JsonValueKind.Number
                then Some (remainingProp.GetDouble())
                else None
            let reset =
                let mutable p = new JsonElement()
                if qiProp.TryGetProperty("resetTime", &p) && p.ValueKind = JsonValueKind.String
                then
                    let s = p.GetString()
                    if not (String.IsNullOrEmpty(s)) then DateParser.formatCountdown s else "Never Resets"
                else "Never Resets"
            Some (remaining, reset)

    /// A single raw model entry: the (key, displayName, modelProvider,
    /// remainingFraction, resetTime) tuple. We collect these first then
    /// group them, so that groups are stable.
    type private RawEntry = {
        ModelId: string
        DisplayName: string
        ModelProvider: string
        RemainingFraction: float option
        ResetCountdown: string
    }

    let private parseRawModel (modelId: string) (parent: JsonElement) : RawEntry option =
        let displayName = getDisplayName parent modelId
        let modelProvider = getStringField parent "modelProvider"
        // Filter placeholder models: chat_*, tab_*, MODEL_PLACEHOLDER_*
        let mid = (if String.IsNullOrEmpty modelId then "" else modelId).ToLowerInvariant()
        if mid.StartsWith("chat_") || mid.StartsWith("tab_") || mid.StartsWith("model_placeholder_") then
            None
        else
            // Filter Google "image" variants (e.g. "Gemini 3.1 Flash Image")
            // - the CLI's Switch Model menu hides these, and the API
            // exposes them as a separate capability-gated endpoint.
            if modelProvider = "MODEL_PROVIDER_GOOGLE" && mid.Contains("image") then
                None
            else
                // Skip Gemini 2.x models - the CLI hides the older generation
                // once Gemini 3.x is available.
                if modelProvider = "MODEL_PROVIDER_GOOGLE" && mid.Contains("gemini-2") then
                    None
                else
                    match parseQuotaInfo parent with
                    | None -> None
                    | Some (remaining, reset) ->
                        Some {
                            ModelId = modelId
                            DisplayName = displayName
                            ModelProvider = modelProvider
                            RemainingFraction = remaining
                            ResetCountdown = reset
                        }

    let private groupKey (entry: RawEntry) : string =
        // Same GroupLabel AND same reset time = same group. GroupLabel is
        // derived from modelProvider (e.g. MODEL_PROVIDER_ANTHROPIC and
        // MODEL_PROVIDER_OPENAI both collapse to "Claude & GPT"), so this
        // key keeps the user's mental model intact: one row per
        // (family, window) pair, not one row per (provider, window).
        providerToGroup entry.ModelProvider + "|" + entry.ResetCountdown

    /// Parses the full fetchAvailableModels response into a list of
    /// grouped buckets. Filters out placeholder models and entries with
    /// no quotaInfo. Groups by (modelProvider, resetTime).
    let parse (root: JsonElement) : Bucket list =
        let mutable modelsProp = new JsonElement()
        if not (root.TryGetProperty("models", &modelsProp)) || modelsProp.ValueKind <> JsonValueKind.Object then
            []
        else
            let raws =
                modelsProp.EnumerateObject()
                |> Seq.choose (fun prop -> parseRawModel prop.Name prop.Value)
                |> Seq.toList
            // Group by key
            let groups = System.Collections.Generic.Dictionary<string, RawEntry list>()
            for entry in raws do
                let k = groupKey entry
                if groups.ContainsKey k then
                    let mutable existing : RawEntry list = Unchecked.defaultof<_>
                    if groups.TryGetValue(k, &existing) then
                        groups.[k] <- entry :: existing
                else
                    groups.[k] <- [entry]
            // Project each group to a Bucket
            let mutable result : Bucket list = []
            for kv in groups do
                let entries = kv.Value
                let first = List.head entries
                let groupLabel = providerToGroup first.ModelProvider
                // Sort entries by remaining fraction ascending so the
                // most-used model is listed first.
                let sorted =
                    entries
                    |> List.sortBy (fun e ->
                        match e.RemainingFraction with
                        | Some f -> f
                        | None -> 1.0)
                // Dedupe by displayName - Google's API returns multiple
                // modelId entries for the same user-facing model (e.g.
                // "gemini-2.5-flash" and "gemini-2.5-flash-thinking" both
                // surface as "Gemini 3.1 Flash Lite"). Keep only the first
                // occurrence of each displayName within a group.
                let sortedByName =
                    sorted
                    |> List.ofSeq
                    |> List.distinctBy (fun e -> e.DisplayName)
                let memberNames =
                    sortedByName
                    |> List.map (fun e -> e.DisplayName)
                    |> String.concat ", "
                let primaryModel = (List.head sortedByName).DisplayName
                // Group's remainingFraction = the MIN of all members'
                // remainingFraction (most-pressed), since they share the
                // quota. If any member has no fraction, the group is
                // considered hit (0% remaining).
                let remaining =
                    if List.exists (fun e -> e.RemainingFraction.IsNone) entries then
                        0.0
                    else
                        entries
                        |> List.map (fun e -> e.RemainingFraction |> Option.defaultValue 0.0)
                        |> List.min
                let used = Math.Clamp((1.0 - remaining) * 100.0, 0.0, 100.0)
                let remainingPct = Math.Clamp(remaining * 100.0, 0.0, 100.0)
                result <- {
                    GroupLabel = groupLabel
                    PrimaryModel = primaryModel
                    Members = memberNames
                    UsedPercent = used
                    RemainingPercent = remainingPct
                    ResetCountdown = first.ResetCountdown
                } :: result
            List.rev result

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

    let private readBucket (parent: JsonElement) (name: string) : Bucket =
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
                    countdown <- DateParser.formatCountdown s
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
        let session = readBucket root "five_hour"
        let weekly = readBucket root "seven_day"

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
    // Credential Manager and calls v1internal:fetchAvailableModels, the
    // same endpoint the `agy models` CLI uses. Each model becomes its own
    // UsageWindow.
    and private fetchAntigravityUsage () : Task<ProviderUsage> = task {
        let provider = UsageProvider.Antigravity
        let name = ProviderMapping.getDisplayName provider
        try
            let token = AntigravityCredentials.load ()

            use request = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels")
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
