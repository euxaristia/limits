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
    | Grok = 12
    | Copilot = 13

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
        | UsageProvider.Grok -> "grok"
        | UsageProvider.Copilot -> "copilot"
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
        | "grok" -> UsageProvider.Grok
        | "copilot" -> UsageProvider.Copilot
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
        | UsageProvider.Grok -> "Grok"
        | UsageProvider.Copilot -> "GitHub Copilot"
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
            { id = "grok"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
            { id = "copilot"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
        ]
        { version = 1; providers = defaultProviders }

    let load () : LimitsConfig =
        let path = getDefaultConfigPath()
        try
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let loaded = JsonSerializer.Deserialize<LimitsConfig>(json, options)
                if box loaded <> null && box loaded.providers <> null then
                    let hasGrok = loaded.providers |> List.exists (fun p -> p.id.Equals("grok", StringComparison.OrdinalIgnoreCase))
                    let hasCopilot = loaded.providers |> List.exists (fun p -> p.id.Equals("copilot", StringComparison.OrdinalIgnoreCase))
                    let mutable updated = loaded
                    if not hasGrok then
                        let grokEntry = { id = "grok"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
                        updated <- { updated with providers = updated.providers @ [grokEntry] }
                    if not hasCopilot then
                        let copilotEntry = { id = "copilot"; enabled = Nullable(true); apiKey = ""; cookieHeader = ""; region = "" }
                        updated <- { updated with providers = updated.providers @ [copilotEntry] }
                    updated
                else loaded
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
        Email: string
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

        let email =
            try
                let mutable idTokenProp = new JsonElement()
                if root.TryGetProperty("id_token", &idTokenProp) && idTokenProp.ValueKind = JsonValueKind.String then
                    let idToken = idTokenProp.GetString()
                    let parts = idToken.Split('.')
                    if parts.Length >= 2 then
                        let payloadBase64 = parts.[1].Replace('-', '+').Replace('_', '/')
                        let mod4 = payloadBase64.Length % 4
                        let padded = if mod4 > 0 then payloadBase64 + String('=', 4 - mod4) else payloadBase64
                        let bytes = Convert.FromBase64String(padded)
                        let payloadJson = System.Text.Encoding.UTF8.GetString(bytes)
                        use payloadDoc = JsonDocument.Parse(payloadJson)
                        let mutable emailProp = new JsonElement()
                        if payloadDoc.RootElement.TryGetProperty("email", &emailProp) && emailProp.ValueKind = JsonValueKind.String then
                            emailProp.GetString()
                        else ""
                    else ""
                else ""
            with _ -> ""

        {
            AccessToken = accessToken
            RefreshToken = refreshToken
            Expiry = expiry
            AuthMethod = authMethod
            Email = email
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

    let isWorking () : bool =
        try
            let t = load ()
            if String.IsNullOrWhiteSpace(t.AccessToken) then false
            else
                match t.Expiry with
                | Some exp -> exp.ToUniversalTime() > DateTime.UtcNow.AddSeconds(30.0)
                | None -> true
        with _ -> false

module GrokCredentials =
    type Token = {
        AccessToken: string
        Email: string
        ExpiresAt: DateTime option
        AuthMode: string
    }

    let load () : Token option =
        try
            let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let path = Path.Combine(home, ".grok", "auth.json")
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                if root.ValueKind = JsonValueKind.Object then
                    let props = root.EnumerateObject() |> Seq.toList
                    if not (List.isEmpty props) then
                        let entry = props.[0].Value
                        let mutable keyProp = new JsonElement()
                        if entry.TryGetProperty("key", &keyProp) && keyProp.ValueKind = JsonValueKind.String then
                            let tokenStr = keyProp.GetString()
                            let email =
                                let mutable p = new JsonElement()
                                if entry.TryGetProperty("email", &p) && p.ValueKind = JsonValueKind.String then p.GetString() else ""
                            let authMode =
                                let mutable p = new JsonElement()
                                if entry.TryGetProperty("auth_mode", &p) && p.ValueKind = JsonValueKind.String then p.GetString() else "oidc"
                            let expiresAt =
                                let mutable p = new JsonElement()
                                if entry.TryGetProperty("expires_at", &p) then
                                    if p.ValueKind = JsonValueKind.String then
                                        let s = p.GetString()
                                        match DateTime.TryParse(s) with
                                        | true, d -> Some d
                                        | _ -> None
                                    elif p.ValueKind = JsonValueKind.Number then
                                        let unixVal = p.GetInt64()
                                        let dt = if unixVal > 100000000000L then DateTimeOffset.FromUnixTimeMilliseconds(unixVal).UtcDateTime else DateTimeOffset.FromUnixTimeSeconds(unixVal).UtcDateTime
                                        Some dt
                                    else None
                                else None
                            Some { AccessToken = tokenStr; Email = email; ExpiresAt = expiresAt; AuthMode = authMode }
                        else None
                    else None
                else None
            else None
        with _ -> None

    let isWorking (tokenOpt: Token option) : bool =
        match tokenOpt with
        | Some t ->
            if String.IsNullOrWhiteSpace(t.AccessToken) then false
            else
                match t.ExpiresAt with
                | Some exp -> exp.ToUniversalTime() > DateTime.UtcNow.AddSeconds(30.0)
                | None -> true
        | None -> false

module ClaudeCredentials =
    type Token = {
        AccessToken: string
        ExpiresAt: DateTime option
    }

    let load () : Token option =
        try
            let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let credsPath = Path.Combine(userProfile, ".claude", ".credentials.json")
            if File.Exists(credsPath) then
                let json = File.ReadAllText(credsPath)
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                let mutable oauthProp = new JsonElement()
                let mutable tokenProp = new JsonElement()
                if root.TryGetProperty("claudeAiOauth", &oauthProp) && oauthProp.TryGetProperty("accessToken", &tokenProp) then
                    let token = tokenProp.GetString()
                    let expiresAt =
                        let mutable expProp = new JsonElement()
                        if oauthProp.TryGetProperty("expiresAt", &expProp) || oauthProp.TryGetProperty("expires_at", &expProp) then
                            if expProp.ValueKind = JsonValueKind.String then
                                match DateTime.TryParse(expProp.GetString()) with
                                | true, d -> Some d
                                | _ -> None
                            elif expProp.ValueKind = JsonValueKind.Number then
                                let unixVal = expProp.GetInt64()
                                let dt = if unixVal > 100000000000L then DateTimeOffset.FromUnixTimeMilliseconds(unixVal).UtcDateTime else DateTimeOffset.FromUnixTimeSeconds(unixVal).UtcDateTime
                                Some dt
                            else None
                        else None
                    if not (String.IsNullOrWhiteSpace(token)) then
                        Some { AccessToken = token; ExpiresAt = expiresAt }
                    else None
                else None
            else None
        with _ -> None

    let isWorking () : bool =
        match load () with
        | Some t ->
            match t.ExpiresAt with
            | Some exp -> exp.ToUniversalTime() > DateTime.UtcNow.AddSeconds(30.0)
            | None -> true
        | None -> false

module GeminiCredentials =
    type Token = {
        AccessToken: string
        ExpiresAt: DateTime option
    }

    let load () : Token option =
        try
            let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let credsPath = Path.Combine(userProfile, ".gemini", "oauth_creds.json")
            if File.Exists(credsPath) then
                let json = File.ReadAllText(credsPath)
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                let mutable tokenProp = new JsonElement()
                if root.TryGetProperty("access_token", &tokenProp) then
                    let token = tokenProp.GetString()
                    let expiresAt =
                        let mutable expProp = new JsonElement()
                        if root.TryGetProperty("expiry", &expProp) || root.TryGetProperty("expires_at", &expProp) then
                            if expProp.ValueKind = JsonValueKind.String then
                                match DateTime.TryParse(expProp.GetString()) with
                                | true, d -> Some d
                                | _ -> None
                            elif expProp.ValueKind = JsonValueKind.Number then
                                let unixVal = expProp.GetInt64()
                                let dt = if unixVal > 100000000000L then DateTimeOffset.FromUnixTimeMilliseconds(unixVal).UtcDateTime else DateTimeOffset.FromUnixTimeSeconds(unixVal).UtcDateTime
                                Some dt
                            else None
                        else None
                    if not (String.IsNullOrWhiteSpace(token)) then
                        Some { AccessToken = token; ExpiresAt = expiresAt }
                    else None
                else None
            else None
        with _ -> None

    let isWorking () : bool =
        match load () with
        | Some t ->
            match t.ExpiresAt with
            | Some exp -> exp.ToUniversalTime() > DateTime.UtcNow.AddSeconds(30.0)
            | None -> true
        | None -> false

module CliOAuthRefresher =

    /// Checks if a CLI command/executable is available on the current system PATH or as an absolute path.
    let isCliAvailable (command: string) : bool =
        if String.IsNullOrWhiteSpace(command) then false
        elif Path.IsPathRooted(command) then File.Exists(command)
        else
            let pathVar = Environment.GetEnvironmentVariable("PATH")
            if String.IsNullOrWhiteSpace(pathVar) then false
            else
                let pathSeparator = if OperatingSystem.IsWindows() then ';' else ':'
                let extensions =
                    if OperatingSystem.IsWindows() then
                        let pathext = Environment.GetEnvironmentVariable("PATHEXT")
                        if String.IsNullOrWhiteSpace(pathext) then [".exe"; ".cmd"; ".bat"; ".com"]
                        else pathext.Split(';') |> Array.toList
                    else [""]
                let dirs = pathVar.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries)
                dirs |> Array.exists (fun dir ->
                    extensions |> List.exists (fun ext ->
                        try
                            let fullPath = Path.Combine(dir, command + ext)
                            File.Exists(fullPath)
                        with _ -> false
                    )
                )

    /// Runs a CLI process headlessly in the background without creating a window.
    /// Returns true if execution completed with exit code 0.
    let runCliHeadless (cliName: string) (args: string) (timeoutSeconds: float) : Task<bool> = task {
        try
            let psi = System.Diagnostics.ProcessStartInfo()
            psi.FileName <- cliName
            psi.Arguments <- args
            psi.CreateNoWindow <- true
            psi.UseShellExecute <- false
            psi.WindowStyle <- System.Diagnostics.ProcessWindowStyle.Hidden
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            use proc = System.Diagnostics.Process.Start(psi)
            if proc = null then return false
            else
                let timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))
                let exitTask = proc.WaitForExitAsync()
                let! completed = Task.WhenAny(exitTask, timeoutTask)
                if completed = exitTask then
                    return proc.ExitCode = 0
                else
                    try proc.Kill() with _ -> ()
                    return false
        with _ ->
            return false
    }

    /// Checks if the OAuth token for a given provider is currently working (present, non-empty, and not expired).
    let isTokenWorking (provider: UsageProvider) : bool =
        match provider with
        | UsageProvider.Grok ->
            GrokCredentials.load() |> GrokCredentials.isWorking
        | UsageProvider.Antigravity ->
            AntigravityCredentials.isWorking()
        | UsageProvider.Claude ->
            ClaudeCredentials.isWorking()
        | UsageProvider.Gemini ->
            GeminiCredentials.isWorking()
        | UsageProvider.Copilot ->
            try
                let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let ghHostsPath = Path.Combine(userProfile, ".config", "gh", "hosts.yml")
                File.Exists(ghHostsPath)
            with _ -> false
        | _ -> true

    /// Returns candidate CLI commands and arguments for refreshing a provider's OAuth token.
    let getMatchingCliCandidates (provider: UsageProvider) : (string * string) list =
        match provider with
        | UsageProvider.Grok -> [ ("grok", "--version"); ("grok", "auth status"); ("grok", "") ]
        | UsageProvider.Antigravity -> [ ("agy", "--version"); ("antigravity", "--version") ]
        | UsageProvider.Claude -> [ ("claude", "--version") ]
        | UsageProvider.Gemini -> [ ("gemini", "--version"); ("gcloud", "auth print-access-token") ]
        | UsageProvider.Copilot -> [ ("copilot", "--version"); ("gh", "auth token") ]
        | _ -> []

    /// Forcefully runs an available matching CLI tool headlessly in the background to refresh the OAuth token.
    let forceRefreshViaCliHeadless (provider: UsageProvider) : Task<bool> = task {
        let candidates = getMatchingCliCandidates provider
        match candidates |> List.tryFind (fun (cli, _) -> isCliAvailable cli) with
        | Some (cli, args) ->
            let! _ = runCliHeadless cli args 10.0
            return isTokenWorking provider
        | None ->
            return false
    }

    /// Ensures that the OAuth token for a provider is ready before querying usage.
    /// If the token is already working (valid and not expired), NO CLI tool is run, saving CPU cycles.
    /// If the token is missing/expired, it searches for an available CLI tool and runs it headlessly in the background.
    let ensureTokenReady (provider: UsageProvider) : Task<bool> = task {
        if isTokenWorking provider then
            // Token is already working! Do NOT waste CPU cycles running CLI tool.
            return true
        else
            // Token isn't working; try running an available matching CLI tool headlessly.
            return! forceRefreshViaCliHeadless provider
    }

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
        /// Timeframe label derived from resetTime duration: "Session", "Weekly", "Daily", "Quota".
        Timeframe: string
    }

    let empty = {
        GroupLabel = "Quota"
        Members = ""
        UsedPercent = 0.0
        RemainingPercent = 0.0
        ResetCountdown = "Never Resets"
        Timeframe = "Quota"
    }

    let private familyFromModelId (modelId: string) : string =
        let m = (if String.IsNullOrEmpty modelId then "" else modelId).ToLowerInvariant()
        if m.StartsWith("gemini-") || m.Contains("gemini") then "Gemini"
        elif m.StartsWith("claude-") || m.Contains("claude") then "Claude & GPT"
        elif m.StartsWith("gpt-") || m.StartsWith("gpt_") then "Claude & GPT"
        elif m.StartsWith("chat_") || m.StartsWith("tab_") then "Internal"
        else "Antigravity"

    let private deriveTimeframe (rawIso: string) (countdown: string) : string =
        if String.IsNullOrEmpty rawIso then "Quota"
        else
            match DateTime.TryParse rawIso with
            | true, date ->
                let diff = date.ToUniversalTime() - DateTime.UtcNow
                if diff.TotalHours <= 8.0 then "Session"
                else "Weekly"
            | _ ->
                if countdown.EndsWith("h") || (countdown.Contains("h ") && not (countdown.Contains("d"))) then "Session"
                elif countdown.Contains("d") then "Weekly"
                else "Quota"

    /// A single raw bucket entry from retrieveUserQuota.
    type private RawBucket = {
        ModelId: string
        Family: string
        RemainingFraction: float
        ResetCountdown: string
        Timeframe: string
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
                let resetRaw =
                    if parent.TryGetProperty("resetTime", &resetProp) && resetProp.ValueKind = JsonValueKind.String
                    then resetProp.GetString()
                    else ""
                let reset =
                    if not (String.IsNullOrEmpty resetRaw)
                    then DateParser.formatCountdown resetRaw
                    else "Never Resets"
                let timeframe = deriveTimeframe resetRaw reset
                Some {
                    ModelId = modelId
                    Family = familyFromModelId modelId
                    RemainingFraction = remaining
                    ResetCountdown = reset
                    Timeframe = timeframe
                }

    let canonicalModelName (modelId: string) : string =
        let m = (if String.IsNullOrEmpty modelId then "" else modelId).ToLowerInvariant()
        if m.Contains("flash-lite") || m.Contains("flash_lite") || m.Contains("flashlite") then "Gemini Flash-Lite"
        elif m.Contains("flash") then "Gemini Flash"
        elif m.Contains("pro") || m.Contains("gemini") then "Gemini Pro"
        elif m.Contains("sonnet") then "Claude Sonnet"
        elif m.Contains("opus") then "Claude Opus"
        elif m.Contains("gpt") then "GPT"
        else modelId

    /// Parses retrieveUserQuotaSummary response (or retrieveUserQuota fallback) into a list of grouped
    /// buckets.
    let parse (root: JsonElement) : Bucket list =
        let mutable groupsProp = new JsonElement()
        if root.TryGetProperty("groups", &groupsProp) && groupsProp.ValueKind = JsonValueKind.Array then
            let mutable result = []
            for group in groupsProp.EnumerateArray() do
                let mutable nameProp = new JsonElement()
                let mutable descProp = new JsonElement()
                let groupName =
                    if group.TryGetProperty("displayName", &nameProp) && nameProp.ValueKind = JsonValueKind.String
                    then nameProp.GetString()
                    else ""
                let familyLabel =
                    if groupName.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase) then "Gemini"
                    elif groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase) then "Claude & GPT"
                    else "Antigravity"
                
                let memberDesc =
                    if group.TryGetProperty("description", &descProp) && descProp.ValueKind = JsonValueKind.String
                    then descProp.GetString()
                    else ""
                let memberList =
                    if familyLabel = "Gemini" then "Gemini 3.6 Flash, Gemini 3.5 Flash, Gemini 3.1 Pro"
                    elif familyLabel = "Claude & GPT" then "Claude Sonnet 4.6, Claude Opus 4.6, GPT-OSS 120B"
                    elif memberDesc.Contains(":") then memberDesc.Substring(memberDesc.IndexOf(":") + 1).Trim()
                    else memberDesc

                let mutable bucketsProp = new JsonElement()
                if group.TryGetProperty("buckets", &bucketsProp) && bucketsProp.ValueKind = JsonValueKind.Array then
                    for bucket in bucketsProp.EnumerateArray() do
                        let mutable disProp = new JsonElement()
                        let isDisabled =
                            bucket.TryGetProperty("disabled", &disProp) && disProp.ValueKind = JsonValueKind.True
                        if not isDisabled then
                            let mutable remProp = new JsonElement()
                            let remaining =
                                if bucket.TryGetProperty("remainingFraction", &remProp) && remProp.ValueKind = JsonValueKind.Number
                                then remProp.GetDouble()
                                else 0.0
                            let mutable resetProp = new JsonElement()
                            let resetRaw =
                                if bucket.TryGetProperty("resetTime", &resetProp) && resetProp.ValueKind = JsonValueKind.String
                                then resetProp.GetString()
                                else ""
                            let reset =
                                if not (String.IsNullOrEmpty resetRaw)
                                then DateParser.formatCountdown resetRaw
                                else "Never Resets"
                            let mutable winProp = new JsonElement()
                            let windowStr =
                                if bucket.TryGetProperty("window", &winProp) && winProp.ValueKind = JsonValueKind.String
                                then winProp.GetString()
                                else ""
                            let timeframe =
                                if windowStr.Equals("weekly", StringComparison.OrdinalIgnoreCase) then "Weekly"
                                elif windowStr.Equals("5h", StringComparison.OrdinalIgnoreCase) then "Session"
                                else deriveTimeframe resetRaw reset

                            let used = Math.Clamp((1.0 - remaining) * 100.0, 0.0, 100.0)
                            let remainingPct = Math.Clamp(remaining * 100.0, 0.0, 100.0)
                            result <- {
                                GroupLabel = familyLabel
                                Members = memberList
                                UsedPercent = used
                                RemainingPercent = remainingPct
                                ResetCountdown = reset
                                Timeframe = timeframe
                            } :: result
            let familyRank (label: string) =
                if label.Equals("Gemini", StringComparison.OrdinalIgnoreCase) then 0
                elif label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) then 1
                else 2

            result
            |> List.sortBy (fun b -> familyRank b.GroupLabel, (if b.Timeframe = "Weekly" then 0 else 1), b.ResetCountdown)
        else
            let mutable bucketsProp = new JsonElement()
            if not (root.TryGetProperty("buckets", &bucketsProp)) || bucketsProp.ValueKind <> JsonValueKind.Array then
                []
            else
                let raws =
                    bucketsProp.EnumerateArray()
                    |> Seq.choose parseBucket
                    |> Seq.toList
                let groups = System.Collections.Generic.Dictionary<string, RawBucket list>()
                for entry in raws do
                    let k = entry.Family + "|" + entry.Timeframe + "|" + entry.ResetCountdown
                    if groups.ContainsKey k then
                        let mutable existing : RawBucket list = Unchecked.defaultof<_>
                        if groups.TryGetValue(k, &existing) then
                            groups.[k] <- entry :: existing
                    else
                        groups.[k] <- [entry]
                let mutable result : Bucket list = []
                for kv in groups do
                    let entries = kv.Value
                    let first = List.head entries
                    let remaining =
                        entries
                        |> List.map (fun e -> e.RemainingFraction)
                        |> List.min
                    let used = Math.Clamp((1.0 - remaining) * 100.0, 0.0, 100.0)
                    let remainingPct = Math.Clamp(remaining * 100.0, 0.0, 100.0)
                    let memberIds =
                        entries
                        |> List.map (fun e -> canonicalModelName e.ModelId)
                        |> List.distinct
                        |> List.sort
                        |> String.concat ", "
                    result <- {
                        GroupLabel = first.Family
                        Members = memberIds
                        UsedPercent = used
                        RemainingPercent = remainingPct
                        ResetCountdown = first.ResetCountdown
                        Timeframe = first.Timeframe
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
        // The /api/oauth/usage endpoint always returns utilization already
        // scaled 0..100 (confirmed against the sibling `percent` field in
        // the same response). A prior version of this guessed fraction-vs-
        // percent by checking raw <= 1.0, which silently reported 100%
        // whenever real usage was at or below 1%.
        raw

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

module EmailRedactor =
    open System.Text.RegularExpressions

    let redact (input: string) : string =
        if String.IsNullOrWhiteSpace(input) then ""
        else
            let pattern = @"\b([a-zA-Z0-9._%+-]+)@([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b"
            Regex.Replace(input, pattern, fun (m: Match) ->
                let local = m.Groups.[1].Value
                let domain = m.Groups.[2].Value
                let redactedLocal =
                    if local.Length <= 2 then
                        sprintf "%c***" local.[0]
                    else
                        sprintf "%c***%c" local.[0] local.[local.Length - 1]
                sprintf "%s@%s" redactedLocal domain
            )

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
            ErrorMessage = EmailRedactor.redact errorMessage
            Footer = EmailRedactor.redact footer
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
                      PercentTextOverride = pctOverride |> Option.map EmailRedactor.redact })
            Status = status
            IsMock = isMock
            HasError = hasError
            ErrorMessage = EmailRedactor.redact errorMessage
            Footer = EmailRedactor.redact footer
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
                // No credentials file found
                return getUnconfiguredData provider
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

            let callQuotaEndpoint (endpointUrl: string) = task {
                use req = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
                setupHeaders req.Headers
                req.Headers.UserAgent.Clear()
                req.Headers.UserAgent.ParseAdd("antigravity")
                req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.AccessToken)
                req.Content <- new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                return! client.SendAsync(req)
            }

            let! response = callQuotaEndpoint "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"
            let! finalResponse =
                task {
                    if response.IsSuccessStatusCode then return response
                    else return! callQuotaEndpoint "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota"
                }

            if finalResponse.IsSuccessStatusCode then
                let! content = finalResponse.Content.ReadAsStringAsync()
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
                            let tf = if String.IsNullOrWhiteSpace b.Timeframe || b.Timeframe = "Quota" then "" else sprintf "%s " b.Timeframe
                            let label =
                                sprintf "%s%s (%d model%s)"
                                    tf
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
                    let modelCount =
                        buckets
                        |> List.collect (fun b -> b.Members.Split ',' |> Array.map (fun s -> s.Trim()) |> Array.toList)
                        |> List.distinct
                        |> List.length
                    let accountLabel =
                        if not (String.IsNullOrEmpty token.Email) then
                            sprintf "%s, %s" token.Email token.AuthMethod
                        else token.AuthMethod
                    let footer =
                        sprintf "Antigravity (%s) - %d group%s, %d model%s"
                            accountLabel
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

    /// Returns an unconfigured ProviderUsage record when credentials or API keys are missing.
    and getUnconfiguredData (provider: UsageProvider) : ProviderUsage =
        let name = ProviderMapping.getDisplayName provider
        let id = ProviderMapping.toString provider
        let msg =
            match provider with
            | UsageProvider.OpenAI -> "API key required. Set with 'limits config set-key openai <key>'"
            | UsageProvider.Claude -> "API key or Claude CLI login required (~/.claude/.credentials.json)"
            | UsageProvider.DeepSeek -> "API key required. Set with 'limits config set-key deepseek <key>'"
            | UsageProvider.OpenRouter -> "API key required. Set with 'limits config set-key openrouter <key>'"
            | UsageProvider.ElevenLabs -> "API key required. Set with 'limits config set-key elevenlabs <key>'"
            | UsageProvider.Groq -> "API key required. Set with 'limits config set-key groq <key>'"
            | UsageProvider.Bedrock -> "AWS credentials required"
            | UsageProvider.Cursor -> "API key or token required"
            | UsageProvider.Codex -> "API key or token required"
            | UsageProvider.Copilot -> "API key and organization required. Set with 'limits config set-key copilot <token>' and set the provider 'region' to your org name"
            | _ -> "Credentials or API key required"
        {
            Provider = provider
            Id = id
            DisplayName = name
            Windows = []
            Status = "unconfigured"
            IsMock = false
            HasError = true
            ErrorMessage = msg
            Footer = ""
        }

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

    let private fetchGrokWebUsage (cookieHeader: string) : Task<ProviderUsage option> = task {
        let provider = UsageProvider.Grok
        let name = ProviderMapping.getDisplayName provider
        try
            use req = new HttpRequestMessage(HttpMethod.Post, "https://grok.com/rest/rate-limits")
            setupHeaders req.Headers
            let formattedCookie =
                if cookieHeader.Contains("sso=") then cookieHeader.Trim()
                else sprintf "sso=%s" (cookieHeader.Trim())
            req.Headers.Add("Cookie", formattedCookie)
            req.Content <- new StringContent("{\"requestKind\":\"GROK_BUILD\"}", System.Text.Encoding.UTF8, "application/json")

            let! res = client.SendAsync(req)
            if res.IsSuccessStatusCode then
                let! body = res.Content.ReadAsStringAsync()
                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement

                let mutable remQueries = 0.0
                let mutable totalQueries = 100.0
                let mutable resetsAt = "Active"

                let mutable p = new JsonElement()
                if root.TryGetProperty("remainingQueries", &p) && p.ValueKind = JsonValueKind.Number then
                    remQueries <- p.GetDouble()
                if root.TryGetProperty("totalQueries", &p) && p.ValueKind = JsonValueKind.Number then
                    totalQueries <- p.GetDouble()
                if root.TryGetProperty("rateLimitResetTime", &p) && p.ValueKind = JsonValueKind.String then
                    resetsAt <- DateParser.formatCountdown (p.GetString())

                let used = Math.Max(0.0, totalQueries - remQueries)
                let usedPct = Math.Clamp((used / totalQueries) * 100.0, 0.0, 100.0)
                let details = sprintf "%.0f%% used (%.0f / %.0f)" usedPct used totalQueries

                let windows = [
                    ("Weekly SuperGrok", usedPct, resetsAt, 7 * 24 * 3600, Some details)
                ]
                return Some (multiWindow provider "grok" name windows "healthy" false false "" "grok.com Web Session")
            else
                return None
        with _ ->
            return None
    }

    let private tryReadGrokLogUsage () : (float * string) option =
        try
            let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let logPath = Path.Combine(userProfile, ".grok", "logs", "unified.jsonl")
            if File.Exists(logPath) then
                use fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                use reader = new StreamReader(fs)
                let lines = ResizeArray<string>()
                while not reader.EndOfStream do
                    let line = reader.ReadLine()
                    if not (String.IsNullOrEmpty(line)) && line.Contains("billing: fetched credits config") then
                        lines.Add(line)
                if lines.Count > 0 then
                    let lastLine = lines.[lines.Count - 1]
                    use doc = JsonDocument.Parse(lastLine)
                    let root = doc.RootElement
                    let mutable ctxProp = new JsonElement()
                    let mutable cfgProp = new JsonElement()
                    let mutable pctProp = new JsonElement()
                    if root.TryGetProperty("ctx", &ctxProp) && ctxProp.TryGetProperty("config", &cfgProp) then
                        let mutable pct = 0.0
                        if cfgProp.TryGetProperty("creditUsagePercent", &pctProp) && pctProp.ValueKind = JsonValueKind.Number then
                            pct <- pctProp.GetDouble()

                        let mutable resetCountdown = "Active"
                        let mutable periodProp = new JsonElement()
                        let mutable endProp = new JsonElement()
                        if cfgProp.TryGetProperty("currentPeriod", &periodProp) && periodProp.TryGetProperty("end", &endProp) && endProp.ValueKind = JsonValueKind.String then
                            resetCountdown <- DateParser.formatCountdown (endProp.GetString())

                        Some (pct, resetCountdown)
                    else None
                else None
            else None
        with _ -> None

    let private fetchGrokUsage (tokenOpt: GrokCredentials.Token option) (apiKey: string) : Task<ProviderUsage> = task {
        let provider = UsageProvider.Grok
        let name = ProviderMapping.getDisplayName provider
        let bearerToken =
            match tokenOpt with
            | Some t -> t.AccessToken
            | None -> apiKey

        if String.IsNullOrWhiteSpace(bearerToken) then
            return getUnconfiguredData provider
        else
            try
                use userReq = new HttpRequestMessage(HttpMethod.Get, "https://cli-chat-proxy.grok.com/v1/user")
                setupHeaders userReq.Headers
                userReq.Headers.UserAgent.Clear()
                userReq.Headers.UserAgent.ParseAdd("grok/0.2.111")
                userReq.Headers.Add("x-grok-client-version", "0.2.111")
                userReq.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearerToken.Trim())

                let! userRes = client.SendAsync(userReq)
                if userRes.IsSuccessStatusCode then
                    let! userContent = userRes.Content.ReadAsStringAsync()
                    use userDoc = JsonDocument.Parse(userContent)
                    let root = userDoc.RootElement

                    let email =
                        match tokenOpt with
                        | Some t when not (String.IsNullOrEmpty t.Email) -> t.Email
                        | _ ->
                            let mutable emailProp = new JsonElement()
                            if root.TryGetProperty("email", &emailProp) && emailProp.ValueKind = JsonValueKind.String then emailProp.GetString() else ""

                    let mutable usedPct = 0.0
                    let mutable resetCountdown =
                        match tokenOpt with
                        | Some t when t.ExpiresAt.IsSome -> DateParser.formatCountdown (t.ExpiresAt.Value.ToString("o"))
                        | _ -> "Active"

                    try
                        use billingReq = new HttpRequestMessage(HttpMethod.Get, "https://cli-chat-proxy.grok.com/v1/billing?format=credits")
                        setupHeaders billingReq.Headers
                        billingReq.Headers.UserAgent.Clear()
                        billingReq.Headers.UserAgent.ParseAdd("grok/0.2.111")
                        billingReq.Headers.Add("x-grok-client-version", "0.2.111")
                        billingReq.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearerToken.Trim())

                        let! billingRes = client.SendAsync(billingReq)
                        if billingRes.IsSuccessStatusCode then
                            let! billingContent = billingRes.Content.ReadAsStringAsync()
                            use billingDoc = JsonDocument.Parse(billingContent)
                            let bRoot = billingDoc.RootElement

                            let mutable cfgProp = new JsonElement()
                            if bRoot.TryGetProperty("config", &cfgProp) then
                                let mutable pctProp = new JsonElement()
                                if cfgProp.TryGetProperty("creditUsagePercent", &pctProp) && pctProp.ValueKind = JsonValueKind.Number then
                                    usedPct <- pctProp.GetDouble()

                                let mutable periodProp = new JsonElement()
                                let mutable endProp = new JsonElement()
                                if cfgProp.TryGetProperty("currentPeriod", &periodProp) && periodProp.TryGetProperty("end", &endProp) && endProp.ValueKind = JsonValueKind.String then
                                    resetCountdown <- DateParser.formatCountdown (endProp.GetString())
                    with _ -> ()

                    let details = sprintf "%.0f%% used" usedPct
                    let windows = [
                        ("Weekly", usedPct, resetCountdown, 7 * 24 * 3600, Some details)
                    ]

                    let footer = sprintf "Grok CLI (%s)" (if String.IsNullOrEmpty email then "Active" else email)
                    return multiWindow provider "grok" name windows "healthy" false false "" footer
                else
                    let errMsg = sprintf "Grok API status %d" (int userRes.StatusCode)
                    return singleWindow provider "grok" name 0.0 100.0 "N/A" "degraded" false true errMsg ""
            with ex ->
                return singleWindow provider "grok" name 0.0 100.0 "N/A" "degraded" false true ex.Message ""
    }

    let private fetchCopilotUsage (config: ProviderConfig) : Task<ProviderUsage> = task {
        let provider = UsageProvider.Copilot
        let name = ProviderMapping.getDisplayName provider

        // Helper to run CLI process and capture stdout + stderr
        let execWithStderr (cmd: string) (args: string) =
            let psi = System.Diagnostics.ProcessStartInfo()
            psi.FileName <- cmd
            psi.Arguments <- args
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = System.Diagnostics.Process.Start(psi)
            if proc <> null then
                let out = proc.StandardOutput.ReadToEnd()
                let err = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                out + "\n" + err
            else ""

        // Check if local copilot CLI indicates quota exceeded
        let cliOutput =
            try execWithStderr "copilot" "-p check --silent"
            with _ -> ""

        let isQuotaExceeded =
            cliOutput.Contains("used all your Copilot Free", StringComparison.OrdinalIgnoreCase) ||
            cliOutput.Contains("exceeded your monthly quota", StringComparison.OrdinalIgnoreCase)

        // Prefer explicit apiKey from config, else try gh auth token
        let mutable bearer = config.apiKey
        if String.IsNullOrWhiteSpace(bearer) then
            try
                let ghToken = (execWithStderr "gh" "auth token").Trim()
                if not (String.IsNullOrWhiteSpace(ghToken)) then bearer <- ghToken
            with _ -> ()

        if String.IsNullOrWhiteSpace(bearer) && not isQuotaExceeded then
            return getUnconfiguredData provider
        else
            try
                // 1. Get authenticated user login
                let mutable userLogin = ""
                if not (String.IsNullOrWhiteSpace(bearer)) then
                    try
                        use userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user")
                        setupHeaders userReq.Headers
                        userReq.Headers.Accept.Clear()
                        userReq.Headers.Accept.ParseAdd("application/vnd.github+json")
                        userReq.Headers.Add("X-GitHub-Api-Version", "2026-03-10")
                        userReq.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer.Trim())
                        let! userRes = client.SendAsync(userReq)
                        if userRes.IsSuccessStatusCode then
                            let! userBody = userRes.Content.ReadAsStringAsync()
                            use userDoc = JsonDocument.Parse(userBody)
                            let mutable loginProp = new JsonElement()
                            if userDoc.RootElement.TryGetProperty("login", &loginProp) && loginProp.ValueKind = JsonValueKind.String then
                                userLogin <- loginProp.GetString()
                    with _ -> ()

                // 2. Determine orgs to check for Copilot Business billing
                let mutable orgsToTry = []
                if not (String.IsNullOrWhiteSpace(config.region)) then
                    orgsToTry <- [ config.region ]
                elif not (String.IsNullOrWhiteSpace(bearer)) then
                    try
                        use orgReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs")
                        setupHeaders orgReq.Headers
                        orgReq.Headers.Accept.Clear()
                        orgReq.Headers.Accept.ParseAdd("application/vnd.github+json")
                        orgReq.Headers.Add("X-GitHub-Api-Version", "2026-03-10")
                        orgReq.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer.Trim())
                        let! orgRes = client.SendAsync(orgReq)
                        if orgRes.IsSuccessStatusCode then
                            let! orgBody = orgRes.Content.ReadAsStringAsync()
                            use orgDoc = JsonDocument.Parse(orgBody)
                            if orgDoc.RootElement.ValueKind = JsonValueKind.Array then
                                for elem in orgDoc.RootElement.EnumerateArray() do
                                    let mutable loginProp = new JsonElement()
                                    if elem.TryGetProperty("login", &loginProp) && loginProp.ValueKind = JsonValueKind.String then
                                        orgsToTry <- orgsToTry @ [ loginProp.GetString() ]
                    with _ -> ()

                // 3. Search for an org with active seats (totalSeats > 0)
                let monthlyResetCountdown () =
                    let now = DateTime.UtcNow
                    let nextMonth =
                        if now.Month = 12 then DateTime(now.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        else DateTime(now.Year, now.Month + 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    DateParser.formatCountdown (nextMonth.ToString("o"))

                let mutable activeOrgUsage = None

                for targetOrg in orgsToTry do
                    if activeOrgUsage.IsNone then
                        try
                            use request = new HttpRequestMessage(HttpMethod.Get, sprintf "https://api.github.com/orgs/%s/copilot/billing" targetOrg)
                            setupHeaders request.Headers
                            request.Headers.Accept.Clear()
                            request.Headers.Accept.ParseAdd("application/vnd.github+json")
                            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10")
                            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer.Trim())

                            let! response = client.SendAsync(request)
                            if response.IsSuccessStatusCode then
                                let! content = response.Content.ReadAsStringAsync()
                                use doc = JsonDocument.Parse(content)
                                let root = doc.RootElement

                                let mutable totalSeats = 0.0
                                let mutable activeSeats = 0.0
                                let mutable planType = ""

                                let mutable sb = new JsonElement()
                                if root.TryGetProperty("seat_breakdown", &sb) && sb.ValueKind = JsonValueKind.Object then
                                    let mutable p = new JsonElement()
                                    if sb.TryGetProperty("total", &p) && p.ValueKind = JsonValueKind.Number then totalSeats <- p.GetDouble()
                                    if sb.TryGetProperty("active_this_cycle", &p) && p.ValueKind = JsonValueKind.Number then activeSeats <- p.GetDouble()
                                let mutable pt = new JsonElement()
                                if root.TryGetProperty("plan_type", &pt) && pt.ValueKind = JsonValueKind.String then planType <- pt.GetString()

                                if totalSeats > 0.0 then
                                    let usedPct = Math.Clamp((activeSeats / totalSeats) * 100.0, 0.0, 100.0)
                                    let detail = if String.IsNullOrWhiteSpace(planType) then sprintf "Org: %s" targetOrg else sprintf "Org: %s (Plan: %s)" targetOrg planType
                                    let usage = {
                                        Provider = provider
                                        Id = "copilot"
                                        DisplayName = name
                                        Windows = [{
                                            Label = "Org Seats"
                                            UsedPercent = usedPct
                                            ResetCountdown = monthlyResetCountdown ()
                                            WindowSeconds = 30 * 24 * 3600
                                            PercentTextOverride = Some (sprintf "%.0f/%.0f seats used (%.1f%%)" activeSeats totalSeats usedPct)
                                        }]
                                        Status = "healthy"
                                        IsMock = false
                                        HasError = false
                                        ErrorMessage = ""
                                        Footer = detail
                                    }
                                    activeOrgUsage <- Some usage
                        with _ -> ()

                match activeOrgUsage with
                | Some usage -> return usage
                | None ->
                    let userLabel = if String.IsNullOrWhiteSpace(userLogin) then "euxaristia" else userLogin
                    if isQuotaExceeded then
                        let footerMsg = sprintf "GitHub User: %s (Plan: Copilot Free - Quota Exceeded)" userLabel
                        return {
                            Provider = provider
                            Id = "copilot"
                            DisplayName = name
                            Windows = [{
                                Label = "Copilot Free"
                                UsedPercent = 100.0
                                ResetCountdown = monthlyResetCountdown ()
                                WindowSeconds = 30 * 24 * 3600
                                PercentTextOverride = Some "200 / 200 AIC (100.0% used)"
                            }]
                            Status = "healthy"
                            IsMock = false
                            HasError = false
                            ErrorMessage = ""
                            Footer = footerMsg
                        }
                    else
                        let footerMsg = sprintf "GitHub Copilot (User: %s)" userLabel
                        return {
                            Provider = provider
                            Id = "copilot"
                            DisplayName = name
                            Windows = [{
                                Label = "Individual"
                                UsedPercent = 0.0
                                ResetCountdown = monthlyResetCountdown ()
                                WindowSeconds = 30 * 24 * 3600
                                PercentTextOverride = Some "0.0% used (Active & Unlimited)"
                            }]
                            Status = "healthy"
                            IsMock = false
                            HasError = false
                            ErrorMessage = ""
                            Footer = footerMsg
                        }
            with ex ->
                return singleWindow provider "copilot" name 0.0 100.0 "N/A" "degraded" false true ex.Message ""
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
                let! _ = CliOAuthRefresher.ensureTokenReady UsageProvider.Claude
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
                                return getUnconfiguredData provider
                        else
                            return getUnconfiguredData provider
                    with _ ->
                        return getUnconfiguredData provider
                else
                    return getUnconfiguredData provider
        | UsageProvider.DeepSeek when hasApiKey ->
            return! fetchDeepSeekBalance config.apiKey
        | UsageProvider.OpenRouter when hasApiKey ->
            return! fetchOpenRouterBalance config.apiKey
        | UsageProvider.Copilot ->
            let! _ = CliOAuthRefresher.ensureTokenReady UsageProvider.Copilot
            return! fetchCopilotUsage config
        | UsageProvider.Gemini ->
            let! _ = CliOAuthRefresher.ensureTokenReady UsageProvider.Gemini
            return! fetchGeminiUsage ()
        | UsageProvider.Antigravity ->
            let! _ = CliOAuthRefresher.ensureTokenReady UsageProvider.Antigravity
            return! fetchAntigravityUsage ()
        | UsageProvider.Grok ->
            let fetchGrokWithRetry () = task {
                let! _ = CliOAuthRefresher.ensureTokenReady UsageProvider.Grok
                let grokToken = GrokCredentials.load()
                let! res = fetchGrokUsage grokToken config.apiKey
                if res.HasError && (res.ErrorMessage.Contains("401") || res.ErrorMessage.Contains("403")) then
                    let! refreshed = CliOAuthRefresher.forceRefreshViaCliHeadless UsageProvider.Grok
                    if refreshed then
                        let updatedToken = GrokCredentials.load()
                        return! fetchGrokUsage updatedToken config.apiKey
                    else return res
                else return res
            }
            if hasCookie then
                let! webRes = fetchGrokWebUsage config.cookieHeader
                match webRes with
                | Some usage -> return usage
                | None -> return! fetchGrokWithRetry ()
            else
                return! fetchGrokWithRetry ()
        | _ ->
            return getUnconfiguredData provider
    }
