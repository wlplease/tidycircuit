# Security

Last reviewed: November 1, 2026.

The public TidyCircuit 0.1.1 Windows release is a local, read-only folder review tool. It runs as the current Windows user (`asInvoker`), with no administrator request, service, startup task, or network listener. It reads names, paths, size, and modified time under the folder a person selects; it reads file contents only to hash same-size duplicate candidates at or below 256 MB. The public build does not move or delete files, clear caches, upload scan data, or identify malware.

The newer, unreleased `main` source adds a rule-based local finder and a manual action that asks Windows to move marked regular files to the Recycle Bin. It is not an AI model. It refuses protected Windows/app paths, hidden/system/read-only/linked files, app and script types, and non-fixed drives. It requires confirmation, records file paths and outcomes in `%LOCALAPPDATA%\TidyCircuit\operations.jsonl`, and depends on Windows settings for Recycle Bin recovery. Users must check the Recycle Bin before relying on recovery. It does not determine that a personal file is safe to remove.

The Safety screen only offers to open Windows Security. It does not pass a file to Defender or claim to perform a scan. The System Details screen links users to Windows tools; it does not collect live RAM, battery, or drive-health readings.

## Release trust

The public 0.1.1 Windows release predates the current source target and is unsigned. The desktop source now targets .NET 10, Microsoft's current Long-Term Support release. Build and verify a fresh release from this source before distributing the updated features; the existing public 0.1.1 ZIP is unchanged.

## Optional companion script

The repository also contains `companion/TidyCircuit.ps1`. It is separate from the desktop app and is not included in the ZIP. It can report local system information, ask Defender to scan a user-selected file, and move eligible old `.tmp`/`.log` files from the current user's TEMP folder to a local quarantine when explicitly requested and confirmed. That script is not a full-system cleaner; read its source and companion README before running it. Do not run scripts from untrusted forks.

## Vulnerability reports

Avoid posting private file names, paths, or system reports in public issues. Private vulnerability reporting is enabled for this repository: [Report a vulnerability privately](https://github.com/wlplease/tidycircuit/security/advisories/new). Include affected version/commit, impact, and safe reproduction steps. Please allow maintainers reasonable time to investigate and prepare a fix before public disclosure. Do not include real personal files or credentials.

GitHub private vulnerability reporting, dependency alerts, and automated security fixes are enabled. GitHub CodeQL default setup scans C# with the extended query suite on code changes and weekly. The scans passed; path-injection alerts for local-only paths were reviewed and dismissed as false positives because paths come from the native folder picker or Windows per-user locations, with link skipping and cleanup scope checks. CodeQL does not analyze the website's inline JavaScript or PowerShell companion. These tools can miss vulnerabilities and do not replace careful review. The project does not claim to be “hack-proof,” formally audited, or guaranteed secure. Reported changes still need review and release verification.
