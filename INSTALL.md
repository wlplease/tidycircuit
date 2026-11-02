# Install and uninstall TidyCircuit

## Install the Windows release candidate

1. Download `TidyCircuit-0.2.0-rc.3-win-x64.zip` from the [GitHub prerelease](https://github.com/wlplease/tidycircuit/releases/tag/v0.2.0-rc.3).
2. Optional integrity check: `certutil -hashfile TidyCircuit-0.2.0-rc.3-win-x64.zip SHA256`, then compare the result with the published `.sha256` file.
3. Right-click the ZIP, choose **Extract All**, and extract it to a folder you own (for example, `Downloads\TidyCircuit`).
4. Run `TidyCircuit.exe` from the extracted folder. It is self-contained; no separate .NET runtime or installer is needed.
5. Choose a folder to scan. The first scan only reads file metadata. Review results before exporting, organizing, or moving any files.

This candidate is **unsigned**. Windows SmartScreen may show an unknown-publisher warning. Do not bypass security warnings for a file that did not come from this project's GitHub Releases page. A checksum can detect a changed download but does not prove who published it or guarantee safety.

## Uninstall

1. Close TidyCircuit.
2. Delete the folder where you extracted the app.
3. If you also want to remove local app records, review and separately delete `%LOCALAPPDATA%\TidyCircuit`. It can contain full paths in `operations.jsonl` and the organization Undo record. This data is not removed automatically.
4. Delete exported TSV reports separately from the folder where you saved them.

The portable app creates no Windows service, scheduled task, or startup entry. Files moved to the Recycle Bin or by the organization action are user data and are not removed by uninstall; restore or manage them separately.

## Support

Report bugs at [GitHub Issues](https://github.com/wlplease/tidycircuit/issues/new). Include app and Windows versions, reproduction steps, and exact error text. Do not post private file paths, exported lists, or personal documents. Send security vulnerabilities through the private reporting process in [SECURITY.md](SECURITY.md).
