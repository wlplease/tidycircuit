# Windows companion

The optional PowerShell companion provides actual Windows drive, memory, battery charge, and user TEMP inventory. It runs locally without a server or administrator rights. Battery *health* and cycle count are not yet included.

From PowerShell in the repository root:

```powershell
.\companion\TidyCircuit.ps1 -Action Report -MinAgeDays 7 | Set-Content -Encoding utf8 report.json
```

Import `report.json` in the dashboard. The report contains your computer name and system details, so keep it private. The dashboard reads the file locally and does not upload it.

Review eligible old files:

```powershell
.\companion\TidyCircuit.ps1 -Action Plan -MinAgeDays 7
```

Move one chosen eligible file to local quarantine with a confirmation prompt:

```powershell
.\companion\TidyCircuit.ps1 -Action Quarantine -Id 'C:\Users\you\AppData\Local\Temp\example.tmp' -MinAgeDays 7
```

The command returns an operation ID. Restore by ID:

```powershell
.\companion\TidyCircuit.ps1 -Action Restore -Id '<operation-id>'
```

Quarantine is a move, not permanent deletion. Files may be locked or change after the plan; such actions fail safely. The operation log is `%LOCALAPPDATA%\TidyCircuit\operations.jsonl`. The script never clears browser caches, Windows Update files, or Downloads. Use Storage Sense or the owning application's cleanup controls for those.
