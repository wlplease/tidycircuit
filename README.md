# TidyCircuit

<p align="center"><img src="assets/coastal-banner.svg" alt="TidyCircuit coastal sailboat branding" width="900"></p>

**Make room. Keep control.** TidyCircuit is a local Windows file review and organization app. It helps people understand a chosen folder, identify exact duplicates, and make deliberate, recoverable changes.

[Download 0.2.0 Release Candidate (unsigned)](https://github.com/wlplease/tidycircuit/releases/download/v0.2.0-rc.1/TidyCircuit-0.2.0-rc.1-win-x64.zip) · [Website](https://tidycircuit.vercel.app/) · [Source](https://github.com/wlplease/tidycircuit) · [Privacy](PRIVACY.md) · [Install / uninstall](INSTALL.md) · [Support](https://github.com/wlplease/tidycircuit/issues/new)

## What it does

- Scans a folder you select, shows files as they are found, and reports progress. Large scans can pause, resume, or stop while keeping partial results.
- Filters and sorts by size, age, type, path, duplicate status, and folder. Select all applies only to the results currently shown. Exports a local TSV review list.
- Groups same-size candidates first. Only after you ask, compares files up to 256 MB with local SHA-256 and labels exact matches; hashes and paths stay on your PC.
- Finds recognized browser cache files and older `.tmp` / `.temp` files in the current user's Temp directory. It does not clear Windows or arbitrary app caches.
- Offers a manually approved file organization preview, skips conflicts and protected paths, and records moves for an Undo attempt.
- Offers a read-only Windows snapshot for available RAM, battery, and fixed-drive details.
- Sends malware questions to Windows Security / Microsoft Defender. TidyCircuit does not scan for or diagnose malware.

Cleanup is manual. Before a Recycle Bin action, the app rechecks eligibility and shows the file count, space, exclusions, and recovery caveat for confirmation. It blocks system/app locations, linked paths, hidden/system/read-only files, unsupported drives, and files changed since scanning. Recycle Bin recovery depends on Windows and drive settings. Review carefully and keep independent backups.

## Release candidate status

This is an **unsigned 0.2.0 release candidate**, built as a self-contained Windows x64 ZIP. Windows SmartScreen may warn that the publisher is unknown. The checksum file is beside the ZIP in the GitHub prerelease. A checksum checks file integrity against that release; it does not prove the publisher or certify safety. This release candidate has been exercised on the project PC with disposable files. A separate clean-PC installation test is still pending; do not treat it as a production release.

## Install and uninstall

Extract the ZIP to a folder you own, run `TidyCircuit.exe`, and choose a folder to review. It does not install a Windows service, startup entry, or scheduled task. To uninstall, close the app and remove the extracted app folder. Local operation and organization logs remain under `%LOCALAPPDATA%\TidyCircuit`; exported reports remain wherever you saved them. See [INSTALL.md](INSTALL.md) for the full steps.

## Build from source

Requires the .NET 10 SDK and Windows desktop targeting packs:

```powershell
dotnet publish desktop/TidyCircuit.Desktop/TidyCircuit.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o releases/win-x64
```

## Privacy, support, and license

The desktop app does not upload file lists, file contents, or hashes and does not include cloud AI. The website cannot access your files. See [PRIVACY.md](PRIVACY.md), [SECURITY.md](SECURITY.md), [DISCLAIMER.md](DISCLAIMER.md), and [LICENSE](LICENSE). Report bugs through [GitHub Issues](https://github.com/wlplease/tidycircuit/issues/new); do not attach private file lists.

TidyCircuit is free for early access through **December 12, 2026 at 12:12 UTC**. Existing copies remain usable after that date. The source is MIT-licensed; no payment or license enforcement is currently implemented.
