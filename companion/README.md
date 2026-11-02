# Windows companion

The optional PowerShell companion reports Windows drives, RAM, battery charge, Microsoft Defender status (when available), and a narrow user TEMP inventory. It runs locally without a server or administrator rights. Battery *health* and cycle count are not included.

From PowerShell in the repository root:

```powershell
.\companion\TidyCircuit.ps1 -Action Report -MinAgeDays 30 | Set-Content -Encoding utf8 report.json
```

Import `report.json` in the dashboard. The report contains your computer name and system details, so keep it private. The dashboard reads the file locally and does not upload it.

## Review and recoverable cleanup

```powershell
.\companion\TidyCircuit.ps1 -Action Plan -MinAgeDays 30
```

The plan includes only regular `.tmp` and `.log` files at least 30 days old inside the current user's TEMP directory. Hidden, read-only, system, linked, and known Microsoft, Windows, browser, driver, installer, and app folders are excluded. This is deliberately conservative; it is not a full-system cleanup.

Move one eligible file to quarantine with a confirmation prompt:

```powershell
.\companion\TidyCircuit.ps1 -Action Quarantine -Id 'C:\Users\you\AppData\Local\Temp\example.tmp' -MinAgeDays 30
```

Restore with the returned operation ID:

```powershell
.\companion\TidyCircuit.ps1 -Action Restore -Id '<operation-id>'
```

Quarantine is a move, not permanent deletion. The operation log and quarantined files are under `%LOCALAPPDATA%\TidyCircuit`. If the original path is occupied, restore stops rather than overwriting it. A locked or changed file also fails safely. No automatic purge is implemented.

## Malware checking

TidyCircuit does not detect malware itself. To ask Microsoft Defender to scan one exact file in your user profile:

```powershell
.\companion\TidyCircuit.ps1 -Action Scan -Id 'C:\Users\you\Downloads\example.exe'
```

This requires confirmation. Microsoft Defender may quarantine or otherwise act on detected threats; check **Windows Security → Virus & threat protection → Protection history** for the result. If Microsoft Defender is unavailable or another antivirus is active, use that product's scan command or right-click the file in File Explorer and choose its scan option. Do not use the TidyCircuit duplicate list as a malware verdict. A valid Microsoft signature also does not make an arbitrary file safe to delete.

The companion never clears Windows Update, browser, app, or system cache folders. Use Storage Sense or the owning application for those categories.
