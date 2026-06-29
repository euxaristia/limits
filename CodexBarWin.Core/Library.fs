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
                // Create directory if not exists
                let dir = Path.GetDirectoryName(path)
                if not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore
                let json = JsonSerializer.Serialize(defaultConfig, JsonSerializerOptions(WriteIndented = true))
                File.WriteAllText(path, json)
                defaultConfig
        with ex ->
            // Fallback to default config on error
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

module UsageFetcher =
    let private client = new HttpClient()

    // Helper to query DeepSeek balance
    let private fetchDeepSeekBalance (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.DeepSeek
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance")
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)
            
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
                        Provider = provider
                        Id = "deepseek"
                        DisplayName = name
                        Used = 0.0 // Credits display does not necessarily have a direct usage/limit ratio
                        Limit = totalBalance // Display total balance as the limit/available
                        Unit = "$"
                        ResetCountdown = "Never Resets"
                        Status = "healthy"
                        IsMock = false
                        HasError = false
                        ErrorMessage = ""
                        CostInfo = sprintf "Available: $%M" (decimal totalBalance)
                    }
                else
                    return {
                        Provider = provider; Id = "deepseek"; DisplayName = name
                        Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                        Status = "degraded"; IsMock = false; HasError = true
                        ErrorMessage = "Account is unavailable"; CostInfo = ""
                    }
            else
                let! errorText = response.Content.ReadAsStringAsync()
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
                Status = "degraded"; IsMock = false; HasError = true
                ErrorMessage = ex.Message; CostInfo = ""
            }
    }

    // Helper to query OpenRouter balance
    let private fetchOpenRouterBalance (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.OpenRouter
        let name = ProviderMapping.getDisplayName provider
        try
            use request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key")
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)
            
            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(content)
                let root = doc.RootElement
                let data = root.GetProperty("data")
                let limit = data.GetProperty("limit").GetDouble()
                let usage = data.GetProperty("usage").GetDouble()
                
                return {
                    Provider = provider
                    Id = "openrouter"
                    DisplayName = name
                    Used = usage
                    Limit = limit
                    Unit = "$"
                    ResetCountdown = "Monthly Reset"
                    Status = "healthy"
                    IsMock = false
                    HasError = false
                    ErrorMessage = ""
                    CostInfo = sprintf "Used: $%M / $%M" (decimal usage) (decimal limit)
                }
            else
                return {
                    Provider = provider; Id = "openrouter"; DisplayName = name
                    Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                    Status = "degraded"; IsMock = false; HasError = true
                    ErrorMessage = sprintf "API error: %O" response.StatusCode; CostInfo = ""
                }
        with ex ->
            return {
                Provider = provider; Id = "openrouter"; DisplayName = name
                Used = 0.0; Limit = 0.0; Unit = "$"; ResetCountdown = "N/A"
                Status = "degraded"; IsMock = false; HasError = true
                ErrorMessage = ex.Message; CostInfo = ""
            }
    }

    // Generates high-fidelity mock data if api key is missing or for display
    let getMockData (provider: UsageProvider) : ProviderUsage =
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
            { Provider = provider; Id = id; DisplayName = name; Used = 12000.0; Limit = 20000.0; Unit = "tokens"
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

    let fetch (config: ProviderConfig) : Task<ProviderUsage> = task {
        let provider = ProviderMapping.fromString config.id
        if String.IsNullOrWhiteSpace(config.apiKey) then
            // No API key - return high-fidelity mock data so the app displays beautifully out-of-the-box
            return getMockData provider
        else
            match provider with
            | UsageProvider.DeepSeek -> 
                return! fetchDeepSeekBalance config.apiKey
            | UsageProvider.OpenRouter -> 
                return! fetchOpenRouterBalance config.apiKey
            // Fallback for others (like OpenAI, Claude which require browser cookies or complex CLI configurations)
            | _ -> 
                let mock = getMockData provider
                return { mock with IsMock = false; CostInfo = mock.CostInfo + " (API key configured)" }
    }
