# Limits 🎚️

Every AI coding limit, right in your Windows System Tray.

A native Windows App SDK / WinUI 3 application utilizing an F# Core library for configuration handling and API operations.

## Features

- **Native System Tray Integration**: Lives quietly in your Notification Area via `H.NotifyIcon`.
- **Flyout Dashboard**: Clean, borderless popover window utilizing Windows 11 `MicaBackdrop` that positions itself perfectly above the taskbar.
- **Shared Config**: Reads and writes configuration (`~/.config/limits/config.json` with fallback to `~/.config/codexbar/config.json`), keeping compatibility with legacy CLI configuration profiles.
- **Vibrant Gradients**: Horizontal progress bars showing usage with custom gradient colors per provider (OpenAI, Claude, Gemini, DeepSeek, Cursor).
- **Settings Panel**: Slide-in UI to toggle active providers and configure API keys directly inside the app.
- **Auto-Hide**: Hides to the system tray on window deactivation (clicking away) or closing, and opens on a single click of the tray icon.

## Project Structure

This is a mixed-language .NET 11 solution leveraging the strengths of both F# and C#:

- **`Limits.Core` (F# Library)**: Handles domain models, JSON deserialization of config files, environment-variable overrides, and API/mock usage fetch engines.
- **`Limits` (C# WinUI 3 App)**: Coordinates the Windows App SDK window lifecycle, Win32 handle positioning, programmatic tray icons, and XAML bindings.

## Requirements

- Windows 10/11 (x64 / ARM64)
- .NET 11 SDK or later

## Build

```powershell
dotnet build Limits.csproj
```

## Run

```powershell
dotnet run --project Limits.csproj
```

## Technical Notes

- **XAML Binding Optimization**: Uses explicit `{x:Bind Mode=OneTime}` references to eliminate compilation overhead and avoid change listener leaks.
- **Safe Borderless Windowing**: Employs `OverlappedPresenter` to remove default OS window chrome and handles manual client repositioning via `DisplayArea` bounds.
- **No Console Subsystem**: Compiled with `<OutputType>WinExe</OutputType>` to run entirely as a background window task.
- **Auto-Hide Deactivation**: Subscribes to the `Activated` event to trigger `AppWindow.Hide()` once the window loses focus.
