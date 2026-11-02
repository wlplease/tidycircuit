# Privacy

The public TidyCircuit 0.1.1 Windows build scans only the folder you choose. Newer cleanup features on the unreleased `main` source are described separately below.

## What the app reads

- File names, relative paths, file sizes, and last modified dates in the selected folder and its accessible subfolders.
- File contents only when comparing same-size duplicate candidates with SHA-256. Files larger than 256 MB are skipped for hashing.
- It skips inaccessible paths and reparse points (such as directory links).

## What stays on your PC

The scan list, file paths, file contents used for hashing, and hashes remain in the app process. TidyCircuit does not upload scan data, create an account, or include analytics. The public 0.1.1 app does not move or delete files. Exported review TSVs are saved only where you choose.

The unreleased `main` source includes local rule-based search over the chosen folder and an optional, manually confirmed move of marked items to the Windows Recycle Bin. It does not include an AI model or send file information over the network. For those recycle operations it writes the time, full file paths, byte counts, and outcomes to `%LOCALAPPDATA%\TidyCircuit\operations.jsonl`. This local log may expose private file names and paths to anyone who can access your Windows account. Deleting the app folder does not remove the log. Recycle Bin recovery depends on Windows/drive settings; verify items appear there before relying on recovery.

TidyCircuit does not scan the whole PC, clean Windows or app caches, report system health, or detect malware. The Safety screen can open Windows Security, but it does not perform or report a Defender scan. Use Windows Security to start a malware scan.

## Report a problem

Open a [GitHub issue](https://github.com/wlplease/CleanMeScarlett/issues/new). Please do not attach private file lists or system reports. Include the app version, Windows version, steps to reproduce, and the exact error text.
