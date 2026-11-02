# Privacy

Last updated: November 1, 2026.

## Desktop app

The public TidyCircuit 0.1.1 Windows build scans only the folder you choose. Newer cleanup features on the unreleased `main` source are described separately below.

## What the app reads

- File names, relative paths, file sizes, and last modified dates in the selected folder and its accessible subfolders.
- File contents only when comparing same-size duplicate candidates with SHA-256. Files larger than 256 MB are skipped for hashing.
- It skips inaccessible paths and reparse points (such as directory links).

## What stays on your PC

The scan list, file paths, file contents used for hashing, and hashes remain in the app process. The desktop app does not upload scan data, create an account, or include analytics. The public 0.1.1 app does not move or delete files. Exported review TSVs are saved only where you choose.

The unreleased `main` source includes local rule-based search over the chosen folder and a one-click search of recognized Chrome, Edge, Brave, and Firefox cache folders plus direct `.tmp` and `.temp` files in the current user's Temp folder that are at least seven days old. For the one-click results, only visible, regular, unchanged files matching those rules can be moved; for a folder scan, other selected personal files may also be eligible. Every move requires confirmation. It does not clear Windows caches or arbitrary app caches. Browser cookies, history, passwords, and bookmarks are excluded. The search uses local rules; it does not include an AI model or send file information over the network. For recycle operations it writes the time, full file paths, byte counts, and outcomes to `%LOCALAPPDATA%\TidyCircuit\operations.jsonl`. This local log may expose private file names and paths to anyone who can access your Windows account. Deleting the app folder does not remove the log. Recycle Bin recovery depends on Windows/drive settings; verify items appear there before relying on recovery.

TidyCircuit does not scan the whole PC, clean Windows or general app caches, report system health, or detect malware. The Safety screen can open Windows Security, but it does not perform or report a Defender scan. Use Windows Security to start a malware scan.

## Website

The landing page includes Vercel Web Analytics. When analytics is enabled for the deployed project, it reports aggregated page-view and traffic statistics. Vercel documents that Web Analytics uses anonymized, cookie-free analytics and does not associate page views with an IP address; see [Vercel's analytics privacy information](https://vercel.com/docs/analytics/privacy-policy). The built-in website guide runs in your browser using fixed local answers; it does not send chat questions or file information to an AI service. The website cannot access your PC files. Visiting the site and downloading code involves GitHub and Vercel, which process requests under their own privacy policies.

## Local records and deletion

The desktop operation log includes full paths, byte counts, times, and outcomes for recycle actions. Remove `%LOCALAPPDATA%\TidyCircuit\operations.jsonl` yourself if you want to delete it. Exported reports are also your responsibility to store and remove. These may contain private names or folder paths; do not post them in public issues.

## Contact and report a problem

For product support, open a [GitHub issue](https://github.com/wlplease/tidycircuit/issues/new). Please do not attach private file lists or system reports. Include the app version, Windows version, steps to reproduce, and the exact error text. For a security vulnerability, use the private reporting link in [SECURITY.md](SECURITY.md), not a public issue.
