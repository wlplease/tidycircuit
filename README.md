# TidyCircuit

![TidyCircuit brand mark](assets/wordmark.svg)

### A clearer view of your PC. A calmer way to clean.

TidyCircuit is a free, open-source project for understanding storage and making careful cleanup decisions. The current release is a local folder analyzer and Windows maintenance guide. It does not delete files, run in the background, or upload your file list.

![TidyCircuit desktop dashboard](assets/dashboard-desktop.png)

<details><summary>See the mobile layout</summary>

![TidyCircuit mobile dashboard](assets/dashboard-mobile.png)

</details>

## What you can do now

| Feature | Available today | How it works |
| --- | --- | --- |
| Folder overview | Yes | Choose a folder; see file count, total size, and largest files. |
| Duplicate candidates | Yes | Finds files with the same name and size for manual review. This does **not** prove their contents match. |
| Organization ideas | Yes | Groups selected files by extension. No files are moved. |
| Cache and temporary-file guidance | Yes | Points to the Windows, browser, or app controls that own each cache. |
| RAM and battery health | Guide only | Shows how to use Task Manager and a Windows battery report. |
| Whole-drive scan and cleanup | Planned | Requires a separate Windows application. |

### Try it

1. Open [the dashboard](index.html) in a current version of Edge or Chrome, or serve this repository as a static site.
2. Select **Analyze a folder** and choose a folder you want to inspect.
3. Review the largest files, duplicate candidates, and organization ideas. Decide what to change in Windows File Explorer.

The selected folder stays on your computer. The site reads file names and sizes supplied by the browser, and stores no scan results on a server. Folder selection support varies by browser; HTTPS or localhost is recommended for hosting.

The [brand guide](BRAND.md) includes the logo, colors, and voice for contributors.

## Why this is different

- **Specific recommendations.** A large file is an item to review, never automatic junk.
- **Honest status.** The website does not invent drive, RAM, or battery readings it cannot access.
- **No risky shortcuts.** No registry cleaning, RAM booster, SSD defrag, or automatic removal of personal folders.
- **A visible path to a real cleaner.** The planned Windows companion will show exact targets, estimated space, effects, and recovery options before any action.

## Planned Windows companion

The next major milestone is an optional local Windows application. The design calls for drive and memory details, battery diagnostics, a storage explorer, verified duplicate detection, and a reviewable cleanup queue. File changes will require explicit confirmation. A release intended for public use also needs a threat model, signed binaries, and a documented build process.

See the current [browser interface](index.html) and [implementation](app.js). Contributions and product feedback are welcome through GitHub issues.

## Hosting and project name

The front end is static and can be hosted on GitHub Pages or a suitable static host. Vercel's free Hobby plan has [personal, non-commercial use terms](https://vercel.com/docs/plans/hobby); check them before using it for a public service.

**TidyCircuit is a working project name.** An initial web search did not establish trademark clearance. Before a public launch or a costly rename, search the [USPTO trademark database](https://tmsearch.uspto.gov/) for exact and confusingly similar names, check state registries and existing product use, and consider a US trademark attorney. The [USPTO's clearance guide](https://www.uspto.gov/trademarks/search/comprehensive-clearance-search-similar-trademarks) explains why an internet search alone is insufficient.

## Privacy and safety

The current web app performs read-only analysis in the browser. It does not request administrator access or connect to a cleanup service. Avoid placing private file names in screenshots or issue reports. Future diagnostics should be opt-in and inspectable before export.

