# TidyCircuit

**Make room. Keep control.** TidyCircuit is a Windows desktop file review app with a private, read-only scan.

[Download TidyCircuit for Windows](https://github.com/wlplease/CleanMeScarlett/releases/latest/download/TidyCircuit-0.1.1-win-x64.zip) · [Visit the website](https://tidycircuit.vercel.app/) · [Source code](https://github.com/wlplease/CleanMeScarlett)

## Early access 0.1.1

- Choose a folder and see file count, total size, full paths, and modified dates.
- Search and sort files; mark items and export a TSV review list.
- Verify duplicates using SHA-256 after matching file size. Files above 256 MB are skipped.
- Run on Windows 10 or 11, x64. The ZIP contains a self-contained executable; no .NET install is required.

The app only reads the selected folder. It does not move or delete files, clear Windows or app caches, or detect malware. Use Windows Security for threat scans. It skips inaccessible and linked folders. This early access executable is unsigned, so Windows SmartScreen may show a warning. Download only from this repository's Releases page.

## Install

1. Download and unzip `TidyCircuit-0.1.1-win-x64.zip`.
2. Run `TidyCircuit.exe`.
3. Choose a folder. Scanning begins immediately and shows status in the app.

## Build from source

```powershell
dotnet publish desktop/TidyCircuit.Desktop/TidyCircuit.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o releases/win-x64
```

## Safety and privacy

Scans remain on the PC. Duplicate hashes are calculated locally; paths and hashes are not uploaded. A duplicate is only a review clue; it is not permission to delete either copy. See [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and the [MIT license](LICENSE).

The app is free during early access through **November 11, 2026 at 11:11 UTC**. Existing copies remain usable after the offer ends. The website will pause new free downloads while paid access is prepared. The current GitHub release asset is public and cannot be revoked from people who already have its direct link; checkout and license enforcement are not implemented yet.
