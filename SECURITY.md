# Security

Please avoid posting private file names, paths, or system reports in public issues. For a potential vulnerability, use GitHub's private vulnerability reporting for this repository if enabled. If it is unavailable, open an issue that asks maintainers for a private contact channel without including exploit details.

The web app analyzes user-selected folders in the browser. The Windows companion is a PowerShell script with no network listener. Its quarantine action is limited to old files in the current user's TEMP folder and requires confirmation; the operation log and quarantined contents stay in `%LOCALAPPDATA%\TidyCircuit`.

Do not run scripts from untrusted forks. Review the script before using it. Public binary releases are not available yet; code signing and reproducible packaging remain release requirements.
