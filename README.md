# GPT Usage Tray

A native Windows tray app displaying the percentage of your Codex allowance remaining. It uses the installed official Codex CLI's account interface and existing ChatGPT sign-in. It does not estimate a separate ChatGPT website message cap or API billing balance.

The tray number is the percentage left. Right-click (or left-click) to see authentication, available usage windows, reset dates, and **Authenticate with ChatGPT**. Browser authentication and token renewal are handled by Codex itself. The app does not read, copy, or store your credentials.

- A **lock** means you need to authenticate.
- An **amber number** is the last known reading while an update fails; it retries automatically.
- **!** means usage is temporarily unavailable and there is no previous reading.
- **...** means connecting, or no percentage limit was supplied; the menu explains which.
- With several quota windows, the default is the lowest percentage remaining. The menu allows choosing a particular window. Window names come from their actual durations, including accounts that have only a weekly limit.

Updates every 60 seconds, retries failures with a 15-second to 5-minute backoff, refreshes after sleep, and restarts its own helper when needed. No model prompts are sent to measure usage. It runs as one instance with no terminal window. Windows' notification-icon component restores the icon after Explorer restarts.

## Install / run

Requires Windows x64 with .NET Framework 4.8 and the official Codex CLI installed. The CLI must be discoverable in a standard npm location or on PATH; alternatively, set `GPT_USAGE_CODEX_EXE` to its native executable path.

Run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. No administrator rights, SDK download, or additional UI runtime are needed. It builds with the Windows .NET Framework compiler, installs to `%LOCALAPPDATA%\Programs\GPTUsageTray`, creates a Start menu shortcut, and enables **Start with Windows**. The installer disables CodexBar's startup entry and keeps its previous value in `%LOCALAPPDATA%\GPTUsageTray\codexbar-startup-backup.txt`; CodexBar stays installed.

If Windows places the number in the hidden-icons flyout, drag it beside the clock, or use **Windows tray visibility settings** in the menu.

To quit: use the tray menu or `GPTUsageTray.exe --quit`. To refresh immediately: `GPTUsageTray.exe --refresh`. Disable **Start with Windows** in the menu to prevent automatic startup.

## Build and verify

`build.ps1` compiles `Program.cs` to `bin\GPTUsageTray.exe` using .NET Framework 4.8-compatible APIs. `--self-test` writes `self-test.txt` and a 16/32px icon preview beside the executable. `--smoke-test` verifies the actual official app-server connection and writes a sanitized `smoke-test.json`. With the tray closed, `tests\integration.ps1` tests network outages, recovery, helper termination, expired/missing authentication, missing percentages, single-instance enforcement, and graceful helper shutdown against a local fake server.

The running tray writes `%LOCALAPPDATA%\GPTUsageTray\status.json` for local diagnostics: percentage, authentication state, last refresh, and process IDs. It contains no tokens or account identifiers. It keeps usage readings in memory and does not display cached readings from a previous launch. The CLI is found on PATH / standard npm locations; `GPT_USAGE_CODEX_EXE` can explicitly select an installed native `codex.exe`.

Protocol reference: https://learn.chatgpt.com/docs/app-server (account/read, account/rateLimits/read, account/login/start, account/login/cancel).
