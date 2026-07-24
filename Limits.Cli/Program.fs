namespace Limits.Cli

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Limits.Core

module Terminal =
    let useColor () =
        not (Console.IsOutputRedirected) || Environment.GetEnvironmentVariable("FORCE_COLOR") = "1"

    let cyan s = if useColor() then sprintf "\u001b[36m%s\u001b[0m" s else s
    let green s = if useColor() then sprintf "\u001b[32m%s\u001b[0m" s else s
    let yellow s = if useColor() then sprintf "\u001b[33m%s\u001b[0m" s else s
    let red s = if useColor() then sprintf "\u001b[31m%s\u001b[0m" s else s
    let dim s = if useColor() then sprintf "\u001b[2m%s\u001b[0m" s else s
    let bold s = if useColor() then sprintf "\u001b[1m%s\u001b[0m" s else s

    let renderProgressBar (pct: float) (width: int) =
        let clamped = Math.Clamp(pct, 0.0, 100.0)
        let filledCount = int (Math.Round((clamped / 100.0) * float width))
        let emptyCount = Math.Max(0, width - filledCount)
        let filledStr = String('█', filledCount)
        let emptyStr = String('░', emptyCount)
        let barStr = sprintf "[%s%s]" filledStr emptyStr
        if pct > 90.0 then red barStr
        elif pct > 75.0 then yellow barStr
        else green barStr

module Program =

    let printHelp () =
        printfn "%s" (Terminal.bold "Limits CLI — Cross-platform AI Quota & Limit Monitor")
        printfn "%s" (Terminal.dim "Monitor OpenAI, Claude, Gemini, Antigravity, Cursor, DeepSeek, OpenRouter & more")
        printfn ""
        printfn "%s" (Terminal.bold "USAGE:")
        printfn "  limits [command] [options]"
        printfn ""
        printfn "%s" (Terminal.bold "COMMANDS:")
        printfn "  status (default)        Fetch and display current usage for all active providers"
        printfn "  providers               List all available providers and their enabled state"
        printfn "  config path             Display active config file path"
        printfn "  config show             Display active configuration JSON"
        printfn "  config enable <id>      Enable a provider by ID"
        printfn "  config disable <id>     Disable a provider by ID"
        printfn "  config set-key <id> <k> Set API key for a provider"
        printfn "  help, -h, --help        Show this help message"
        printfn "  version, -v, --version  Show CLI version"
        printfn ""
        printfn "%s" (Terminal.bold "OPTIONS:")
        printfn "  --json                  Output raw JSON format (useful for scripts & status bars)"
        printfn "  -p, --provider <id>     Filter status to a single provider (e.g. -p claude)"
        printfn "  --no-color              Disable colored terminal output"

    let renderUsage (maxLabelWidth: int) (u: ProviderUsage) =
        let statusBadge =
            if u.Status = "unconfigured" then Terminal.dim "[UNCONFIGURED]"
            elif u.HasError then Terminal.red "[ERROR]"
            elif u.Status = "degraded" then Terminal.yellow "[DEGRADED]"
            else Terminal.green "[OK]"

        printfn "%s %s" (Terminal.bold u.DisplayName) statusBadge

        if u.HasError then
            if u.Status = "unconfigured" then
                printfn "  %s" (Terminal.dim u.ErrorMessage)
            else
                printfn "  %s" (Terminal.red (sprintf "Error: %s" u.ErrorMessage))
        else
            for w in u.Windows do
                let bar = Terminal.renderProgressBar w.UsedPercent 20
                let pctText =
                    match w.PercentTextOverride with
                    | Some t -> t
                    | None -> sprintf "%5.1f%%" w.UsedPercent
                let resetText =
                    if String.IsNullOrWhiteSpace(w.ResetCountdown) then ""
                    else sprintf " (%s)" w.ResetCountdown

                let paddedLabel = w.Label.PadRight(maxLabelWidth)
                printfn "  %s %s %s%s" paddedLabel bar pctText (Terminal.dim resetText)

            if not (String.IsNullOrWhiteSpace(u.Footer)) then
                printfn "  %s" (Terminal.dim u.Footer)

        printfn ""

    let fetchAndDisplayStatus (jsonOutput: bool) (targetProvider: string option) : Task<int> = task {
        let config = ConfigStore.load()

        let activeConfigs =
            config.providers
            |> List.filter (fun p -> p.enabled.HasValue && p.enabled.Value)
            |> List.filter (fun p ->
                match targetProvider with
                | Some id -> p.id.Equals(id, StringComparison.OrdinalIgnoreCase)
                | None -> true
            )

        if List.isEmpty activeConfigs then
            if jsonOutput then
                printfn "[]"
            else
                printfn "%s" (Terminal.yellow "No active providers enabled in configuration.")
                printfn "Run 'limits providers' to view available providers or 'limits config enable <id>'."
            return 0
        else
            let! tasks =
                activeConfigs
                |> List.map (fun c -> UsageFetcher.fetch c)
                |> Task.WhenAll

            let isErratic (u: ProviderUsage) =
                u.Status = "unconfigured" || u.Status = "degraded" || u.Status = "error" || u.HasError

            let results =
                tasks
                |> Array.toList
                |> List.filter (fun u -> not (isErratic u))

            if jsonOutput then
                let options = JsonSerializerOptions(WriteIndented = true)
                let json = JsonSerializer.Serialize(results, options)
                printfn "%s" json
            else
                if List.isEmpty results then
                    printfn "%s" (Terminal.yellow "No configured providers active.")
                    printfn "Run 'limits providers' to view available providers or set an API key with 'limits config set-key <id> <key>'."
                else
                    let maxLabelWidth =
                        results
                        |> List.collect (fun u -> u.Windows)
                        |> List.map (fun w -> w.Label.Length)
                        |> function
                            | [] -> 24
                            | lengths -> List.max lengths |> max 24

                    printfn "%s" (Terminal.bold "── AI Limits & Quotas ────────────────────────────────")
                    printfn ""
                    for r in results do
                        renderUsage maxLabelWidth r
            return 0
    }

    let handleConfigCommand (args: string list) : int =
        let config = ConfigStore.load()
        match args with
        | ["path"] ->
            printfn "%s" (ConfigStore.getDefaultConfigPath())
            0
        | ["show"] ->
            let options = JsonSerializerOptions(WriteIndented = true)
            printfn "%s" (JsonSerializer.Serialize(config, options))
            0
        | ["enable"; id] ->
            let updatedProviders =
                config.providers
                |> List.map (fun p ->
                    if p.id.Equals(id, StringComparison.OrdinalIgnoreCase) then
                        { p with enabled = Nullable(true) }
                    else p
                )
            let updatedConfig = { config with providers = updatedProviders }
            ConfigStore.save(updatedConfig)
            printfn "%s" (Terminal.green (sprintf "Enabled provider '%s'." id))
            0
        | ["disable"; id] ->
            let updatedProviders =
                config.providers
                |> List.map (fun p ->
                    if p.id.Equals(id, StringComparison.OrdinalIgnoreCase) then
                        { p with enabled = Nullable(false) }
                    else p
                )
            let updatedConfig = { config with providers = updatedProviders }
            ConfigStore.save(updatedConfig)
            printfn "%s" (Terminal.yellow (sprintf "Disabled provider '%s'." id))
            0
        | ["set-key"; id; key] ->
            let updatedProviders =
                config.providers
                |> List.map (fun p ->
                    if p.id.Equals(id, StringComparison.OrdinalIgnoreCase) then
                        { p with apiKey = key }
                    else p
                )
            let updatedConfig = { config with providers = updatedProviders }
            ConfigStore.save(updatedConfig)
            printfn "%s" (Terminal.green (sprintf "Updated API key for provider '%s'." id))
            0
        | _ ->
            printfn "%s" (Terminal.red "Invalid config subcommand.")
            printfn "Usage: limits config [path | show | enable <id> | disable <id> | set-key <id> <key>]"
            1

    let listProviders () =
        let config = ConfigStore.load()
        printfn "%s" (Terminal.bold "Available Providers:")
        printfn ""
        for p in config.providers do
            let providerEnum = ProviderMapping.fromString p.id
            let displayName = ProviderMapping.getDisplayName providerEnum
            let isEnabled = p.enabled.HasValue && p.enabled.Value
            let statusStr = if isEnabled then Terminal.green "[ENABLED] " else Terminal.dim "[DISABLED]"
            let keyStr = if String.IsNullOrWhiteSpace(p.apiKey) then Terminal.dim "(No API Key)" else Terminal.cyan "(API Key Set)"
            printfn "  %-12s %s %-16s %s" p.id statusStr displayName keyStr
        printfn ""

    let getVersion () =
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()
        let attr = assembly.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
        if attr.Length > 0 then
            let infoAttr = attr.[0] :?> System.Reflection.AssemblyInformationalVersionAttribute
            let v = infoAttr.InformationalVersion
            let idx = v.IndexOf('+')
            if idx > 0 then v.Substring(0, idx) else v
        else
            let v = assembly.GetName().Version
            if v <> null then sprintf "%d.%d.%d" v.Major v.Minor v.Build else "1.0.1"

    [<EntryPoint>]
    let main argv =
        let args = argv |> Array.toList

        let jsonMode = args |> List.exists (fun a -> a = "--json")
        let filterProvider =
            match args |> List.tryFindIndex (fun a -> a = "-p" || a = "--provider") with
            | Some idx when idx + 1 < args.Length -> Some args.[idx + 1]
            | _ -> None

        let cleanArgs =
            args
            |> List.filter (fun a -> a <> "--json" && a <> "--no-color")
            |> fun lst ->
                match filterProvider with
                | Some p -> lst |> List.filter (fun a -> a <> "-p" && a <> "--provider" && a <> p)
                | None -> lst

        match cleanArgs with
        | [] | ["status"] ->
            (fetchAndDisplayStatus jsonMode filterProvider).GetAwaiter().GetResult()
        | ["providers"] | ["list"] ->
            listProviders()
            0
        | "config" :: subArgs ->
            handleConfigCommand subArgs
        | ["-v"] | ["--version"] | ["version"] ->
            printfn "limits v%s" (getVersion())
            0
        | ["-h"] | ["--help"] | ["help"] ->
            printHelp()
            0
        | _ ->
            printfn "%s" (Terminal.red "Unknown command.")
            printfn ""
            printHelp()
            1
