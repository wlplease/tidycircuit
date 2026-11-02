# Privacy

TidyCircuit 0.1.1 is a Windows desktop app that scans only the folder you choose.

## What the app reads

- File names, relative paths, file sizes, and last modified dates in the selected folder and its accessible subfolders.
- File contents only when comparing same-size duplicate candidates with SHA-256. Files larger than 256 MB are skipped for hashing.
- It skips inaccessible paths and reparse points (such as directory links).

## What stays on your PC

The scan list, file paths, file contents used for hashing, and hashes remain in the app process. TidyCircuit does not upload scan data, create an account, or include analytics. The app does not move or delete files. Exported review TSVs are saved only where you choose.

TidyCircuit does not scan the whole PC, clean Windows or app caches, report system health, or detect malware. Use Windows Security for malware scans.

## Report a problem

Open a [GitHub issue](https://github.com/wlplease/CleanMeScarlett/issues/new). Please do not attach private file lists or system reports. Include the app version, Windows version, steps to reproduce, and the exact error text.
