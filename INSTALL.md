# Install and uninstall TidyCircuit

## Install

1. Download `TidyCircuit-0.1.1-win-x64.zip` from [GitHub Releases](https://github.com/wlplease/CleanMeScarlett/releases/tag/v0.1.1).
2. Optionally verify it using `certutil -hashfile TidyCircuit-0.1.1-win-x64.zip SHA256` and compare with `TidyCircuit-0.1.1-win-x64.zip.sha256`.
3. Right-click the ZIP, choose **Extract All**, and choose a folder you own, such as `Downloads\TidyCircuit`.
4. Run `TidyCircuit.exe` from the extracted folder. No installer or separate .NET runtime is required.
5. Choose a folder to scan. Scanning is read-only; review/export never changes the scanned files.

This build is unsigned. Windows may show a SmartScreen warning. The checksum confirms that the downloaded ZIP matches the published release asset; it does not identify the publisher or prove the software is safe.

## Uninstall

Close TidyCircuit and delete the folder where you extracted it. The app does not install a service, scheduled task, or startup entry. Delete any exported review TSV separately if you no longer need it. Your original files are not touched.

## Support

Report bugs at [GitHub Issues](https://github.com/wlplease/CleanMeScarlett/issues). Do not post private file paths or personal documents.
