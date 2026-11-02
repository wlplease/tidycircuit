# Privacy

Last updated: November 1, 2026.

## Desktop app

The 0.2.0 release candidate scans only folders you choose, plus explicitly requested recognized cache/temp locations. It is still an unsigned prerelease.

## What the app reads

- File names, relative paths, file sizes, and last modified dates in the selected folder and its accessible subfolders.
- File contents only when comparing same-size duplicate candidates with SHA-256. Files larger than 256 MB are skipped for hashing.
- It skips inaccessible paths and reparse points (such as directory links).

## What stays on your PC

The scan list, file paths, file contents used for hashing, and hashes remain on the PC. The app does not upload scan data, create an account, or include analytics. Exported review TSVs are saved only where you choose. The read-only system snapshot reads Windows memory, power, and local drive APIs on request; no values are transmitted.

The local rule-based finder checks recognized Chrome, Edge, Brave, and Firefox cache folders plus direct `.tmp` and `.temp` files in the current user's Temp folder that are at least seven days old. Only visible, regular, unchanged files matching those rules can be moved from a cache/temp search. A selected-folder scan can include other personal files. Before each move, the app checks paths and file attributes, skips protected or changed files, and requires confirmation. It does not clear Windows caches or arbitrary app caches. Browser cookies, history, passwords, and bookmarks are excluded. Search uses local rules; it does not include an AI model or send file information over the network. The optional organize action moves selected files to suggested type folders only after confirmation and keeps an Undo record. For recycle operations it writes time, full paths, byte counts, and outcomes to `%LOCALAPPDATA%\TidyCircuit\operations.jsonl`; organization undo paths are stored under `%LOCALAPPDATA%\TidyCircuit\organization-undo.json`. These records may expose private file names and paths to anyone with access to your Windows account. Deleting the app folder does not remove them. Recycle Bin recovery depends on Windows/drive settings; verify items appear there before relying on recovery.

TidyCircuit does not scan the whole PC, clean Windows or general app caches, or detect malware. Its read-only system snapshot displays RAM, battery, and fixed-drive details reported by Windows; it is not a diagnostic or health assessment. The app can open Windows Security, but it does not perform or report a Defender scan. Use Windows Security to start a malware scan.

## Website

The landing page includes Vercel Web Analytics. When analytics is enabled for the deployed project, it reports aggregated page-view and traffic statistics. Vercel documents that Web Analytics uses anonymized, cookie-free analytics and does not associate page views with an IP address; see [Vercel's analytics privacy information](https://vercel.com/docs/analytics/privacy-policy). The built-in website guide runs in your browser using fixed local answers; it does not send chat questions or file information to an AI service. The website cannot access your PC files. Visiting the site and downloading code involves GitHub and Vercel, which process requests under their own privacy policies.

## Local records and deletion

The desktop operation log includes full paths, byte counts, times, and outcomes for recycle actions. Remove `%LOCALAPPDATA%\TidyCircuit\operations.jsonl` yourself if you want to delete it. Exported reports are also your responsibility to store and remove. These may contain private names or folder paths; do not post them in public issues.

## Contact and report a problem

For product support, open a [GitHub issue](https://github.com/wlplease/tidycircuit/issues/new). Please do not attach private file lists or system reports. Include the app version, Windows version, steps to reproduce, and the exact error text. For a security vulnerability, use the private reporting link in [SECURITY.md](SECURITY.md), not a public issue.
