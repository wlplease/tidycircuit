# TidyCircuit

**Make room. Keep control.** TidyCircuit is a private Windows file review app. The public 0.1.1 download is read-only; newer local smart-review and recoverable cleanup work on `main` is not in that download yet.

[Download TidyCircuit for Windows](https://github.com/wlplease/CleanMeScarlett/releases/latest/download/TidyCircuit-0.1.1-win-x64.zip) · [Visit the website](https://tidycircuit.vercel.app/) · [Source code](https://github.com/wlplease/CleanMeScarlett) · [Privacy](PRIVACY.md) · [Disclaimer](DISCLAIMER.md) · [Security](SECURITY.md) · [Install/uninstall](INSTALL.md) · [Report a bug](https://github.com/wlplease/CleanMeScarlett/issues/new)

## Early access 0.1.1

- Choose a folder and see file count, total size, full paths, and modified dates.
- Search and sort files; mark items and export a TSV review list.
- Verify duplicates using SHA-256 after matching file size. Files above 256 MB are skipped.
- Run on Windows 10 or 11, x64. The ZIP contains a self-contained executable; no .NET install is required.

The 0.1.1 download only reads the selected folder. It does not move or delete files, find caches automatically, or detect malware. The unreleased `main` source adds a local rule-based finder, background search for large inventories, and a manually confirmed Recycle Bin action with protected paths and a local operation log. Verified duplicate groups label one path as a keep candidate and the other exact copies as extras; a separate control selects only the visible extras, and nothing is selected automatically. Its one-click temporary-file search checks recognized Chrome, Edge, Brave, and Firefox cache folders plus direct `.tmp` and `.temp` files in the current user's Temp folder that are at least seven days old. It does not clear Windows or arbitrary app caches. Browser cookies, history, passwords, and bookmarks are excluded. The app does not include a language model or upload scan data. Use Windows Security for malware scans. This early access executable is unsigned, so Windows SmartScreen may show a warning. Download only from this repository's Releases page.

## Install

See the step-by-step [install and uninstall guide](INSTALL.md).

## Build from source

```powershell
dotnet publish desktop/TidyCircuit.Desktop/TidyCircuit.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o releases/win-x64
```

Building from source requires the .NET 10 SDK.

## Safety and privacy

Scans remain on the PC. Duplicate hashes are calculated locally; paths and hashes are not uploaded. A duplicate or old/large-file suggestion is only a review clue; TidyCircuit cannot decide that a personal file is safe to remove. See [PRIVACY.md](PRIVACY.md), [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and the [MIT license](LICENSE).

The app is free during early access through **December 12, 2026 at 12:12 UTC**. Existing copies remain usable after the offer ends. The website will pause new free downloads while paid access is prepared. The current GitHub release asset is public and cannot be revoked from people who already have its direct link; checkout and license enforcement are not implemented yet.

## Legal and security notes

TidyCircuit is provided under the MIT License. Read the [product disclaimer](DISCLAIMER.md) for cleanup limits, warranty context, and third-party service notes. Neither open source nor a checksum proves a release is safe or guarantees a result. Current Windows builds are unsigned. See the [security policy](SECURITY.md) for vulnerability reporting and project security status. The landing page includes Vercel Web Analytics; its collection depends on the deployed project's setting. See [PRIVACY.md](PRIVACY.md) for the desktop/site distinction.
