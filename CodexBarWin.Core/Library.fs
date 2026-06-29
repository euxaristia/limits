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

type ProviderUsage = {
    Provider: UsageProvider
    Id: string
    DisplayName: string
    Used: float
    Limit: float
    Unit: string
    ResetCountdown: string
    Status: string
    IsMock: bool
    HasError: bool
    ErrorMessage: string
    CostInfo: string
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
        }

module UsageFetcher =
    let private client = new HttpClient()
    
    // Set modern User-Agent to avoid getting blocked by Cloudflare/WAF
    let private setupHeaders (headers: HttpRequestHeaders) =
        headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
        headers.Accept.ParseAdd("application/json, text/plain, */*")

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
                
                return {
                    Provider = provider; Id = "openai"; DisplayName = name
                    Used = totalUsed; Limit = totalGranted; Unit = "$"
                    ResetCountdown = "Credit Expiry"
                    Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                    CostInfo = sprintf "Available: $%M / $%M" (decimal totalAvailable) (decimal totalGranted)
                }
            else
                return {
                    Provider = provider; Id = "openai"; DisplayName = name
                    Used = 0.0; Limit = 100.0; Unit = "$"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "API returned status %d" (int response.StatusCode)
                    CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "openai"; DisplayName = name
                Used = 0.0; Limit = 100.0; Unit = "$"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
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

                        return {
                            Provider = provider; Id = "claude"; DisplayName = name
                            Used = parsed.PrimaryUsed; Limit = 100.0; Unit = "%"
                            ResetCountdown = parsed.PrimaryReset
                            Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                            CostInfo = parsed.CostInfo
                        }
                    else
                        return {
                            Provider = provider; Id = "claude"; DisplayName = name
                            Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                            Status = "degraded"; IsMock = false; HasError = true
                            ErrorMessage = sprintf "Usage API returned HTTP %d" (int usageResponse.StatusCode)
                            CostInfo = ""
                        }
                else
                    return {
                        Provider = provider; Id = "claude"; DisplayName = name
                        Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                        Status = "degraded"; IsMock = false; HasError = true
                        ErrorMessage = "No organization UUID found"
                        CostInfo = ""
                    }
            else
                return {
                    Provider = provider; Id = "claude"; DisplayName = name
                    Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "Orgs API returned HTTP %d (Invalid sessionKey?)" (int orgResponse.StatusCode)
                    CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "claude"; DisplayName = name
                Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
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
                    
                    return {
                        Provider = provider; Id = "deepseek"; DisplayName = name
                        Used = 0.0; Limit = totalBalance; Unit = "$"
                        ResetCountdown = "Never Resets"
                        Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                        CostInfo = sprintf "Available: $%M" (decimal totalBalance)
                    }
                else
                    return {
                        Provider = provider; Id = "deepseek"; DisplayName = name
                        Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                        Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = "Account is unavailable"; CostInfo = ""
                    }
            else
                return {
                    Provider = provider; Id = "deepseek"; DisplayName = name
                    Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "API returned status %d" (int response.StatusCode); CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "deepseek"; DisplayName = name
                Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
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
                
                return {
                    Provider = provider; Id = "openrouter"; DisplayName = name
                    Used = usage; Limit = limit; Unit = "$"
                    ResetCountdown = "Monthly Reset"
                    Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                    CostInfo = sprintf "Used: $%M / $%M" (decimal usage) (decimal limit)
                }
            else
                return {
                    Provider = provider; Id = "openrouter"; DisplayName = name
                    Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "API returned status %O" response.StatusCode; CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "openrouter"; DisplayName = name
                Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
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
                        return {
                            Provider = provider; Id = "gemini"; DisplayName = name
                            Used = usedPercent; Limit = 100.0; Unit = "%"
                            ResetCountdown = resetTime
                            Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                            CostInfo = "Google Code Assist Quota"
                        }
                    else
                        return {
                            Provider = provider; Id = "gemini"; DisplayName = name
                            Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                            Status = "degraded"; IsMock = false; HasError = true
                            ErrorMessage = sprintf "Quota API returned status %d" (int response.StatusCode)
                            CostInfo = ""
                        }
                else
                    return {
                        Provider = provider; Id = "gemini"; DisplayName = name
                        Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                        Status = "degraded"; IsMock = false; HasError = true
                        ErrorMessage = "Credentials file missing access_token"
                        CostInfo = ""
                    }
            else
                // No credentials file found - generate mock telemetry
                return getMockData provider
        with ex ->
            return {
                Provider = provider; Id = "gemini"; DisplayName = name
                Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
    }

    // Generates high-fidelity mock data if api key is missing
    and getMockData (provider: UsageProvider) : ProviderUsage =
        let name = ProviderMapping.getDisplayName provider
        let id = ProviderMapping.toString provider
        match provider with
        | UsageProvider.Codex ->
            { Provider = provider; Id = id; DisplayName = name; Used = 142.0; Limit = 500.0; Unit = "reqs"
              ResetCountdown = "1h 42m"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Plan: Pro" }
        | UsageProvider.OpenAI ->
            { Provider = provider; Id = id; DisplayName = name; Used = 4.82; Limit = 18.0; Unit = "$"
              ResetCountdown = "Resets in 2d"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Spent: $4.82 of $18.00" }
        | UsageProvider.Claude ->
            { Provider = provider; Id = id; DisplayName = name; Used = 28.0; Limit = 50.0; Unit = "msgs"
              ResetCountdown = "4h 12m"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Plan: Claude Pro" }
        | UsageProvider.Cursor ->
            { Provider = provider; Id = id; DisplayName = name; Used = 384.0; Limit = 500.0; Unit = "fast"
              ResetCountdown = "Resets July 5"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "384 / 500 fast requests" }
        | UsageProvider.Gemini ->
            { Provider = provider; Id = id; DisplayName = name; Used = 60.0; Limit = 100.0; Unit = "%"
              ResetCountdown = "Daily Quota"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "RPM: 15 / 360" }
        | UsageProvider.DeepSeek ->
            { Provider = provider; Id = id; DisplayName = name; Used = 2.15; Limit = 15.0; Unit = "$"
              ResetCountdown = "Never Resets"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Balance: $12.85 available" }
        | UsageProvider.OpenRouter ->
            { Provider = provider; Id = id; DisplayName = name; Used = 0.85; Limit = 5.0; Unit = "$"
              ResetCountdown = "Monthly Reset"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Spent: $0.85" }
        | UsageProvider.ElevenLabs ->
            { Provider = provider; Id = id; DisplayName = name; Used = 42500.0; Limit = 100000.0; Unit = "chars"
              ResetCountdown = "Resets in 12d"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "42,500 characters used" }
        | UsageProvider.Groq ->
            { Provider = provider; Id = id; DisplayName = name; Used = 65.0; Limit = 100.0; Unit = "reqs"
              ResetCountdown = "Minute Limit"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "65 / 100 requests" }
        | UsageProvider.Bedrock ->
            { Provider = provider; Id = id; DisplayName = name; Used = 12.45; Limit = 50.0; Unit = "$"
              ResetCountdown = "Billing Cycle"; Status = "healthy"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "Cost this month: $12.45" }
        | _ ->
            { Provider = provider; Id = id; DisplayName = name; Used = 0.0; Limit = 100.0; Unit = "%"
              ResetCountdown = "Unknown"; Status = "unknown"; IsMock = true; HasError = false; ErrorMessage = ""; CostInfo = "" }

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

                return {
                    Provider = provider; Id = "claude"; DisplayName = name
                    Used = parsed.PrimaryUsed; Limit = 100.0; Unit = "%"
                    ResetCountdown = parsed.PrimaryReset
                    Status = "healthy"; IsMock = false; HasError = false; ErrorMessage = ""
                    CostInfo = parsed.CostInfo
                }
            else
                return {
                    Provider = provider; Id = "claude"; DisplayName = name
                    Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "OAuth API returned HTTP %d" (int response.StatusCode)
                    CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "claude"; DisplayName = name
                Used = 0.0; Limit = 100.0; Unit = "%"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true; ErrorMessage = ex.Message; CostInfo = ""
            }
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
        | _ ->
            return getMockData provider
    }
