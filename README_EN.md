<p align="center">
  <img src="./CoursePlanner/Assets/Square150x150Logo.scale-200.png" width="88" alt="Course Planner icon" />
</p>

<h1 align="center">Course Planner</h1>

<p align="center">
  <strong>Turn course data into comparable, actionable plans.</strong><br />
  A local-first, native Windows workspace for planning course registration.
</p>

<p align="center">
  <a href="./README.md">简体中文</a> ·
  <a href="https://github.com/Gliese-876/Course-Planner/releases/latest">Download</a> ·
  <a href="./docs/json-import-guide.en.md">JSON Import Guide</a>
</p>

<p align="center">
  <a href="https://github.com/Gliese-876/Course-Planner/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Gliese-876/Course-Planner?style=flat-square&label=release" /></a>
  <img alt="WinUI 3" src="https://img.shields.io/badge/WinUI%203-Windows%20App%20SDK-0078D4?style=flat-square" />
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%20x64-1f6feb?style=flat-square" />
  <img alt="MIT License" src="https://img.shields.io/badge/license-MIT-2ea44f?style=flat-square" />
</p>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./docs/posters/course-planner-dark.png" />
  <img src="./docs/posters/course-planner-light.png" alt="Course Planner landscape poster showing the real app in its light theme" />
</picture>

## Meet Course Planner

Course Planner brings semesters, labels, a reusable course library, and multiple candidate plans into one workspace. Try schedules, compare trade-offs, catch time conflicts, arrange registration order, and export the result. Course data stays on your device by default, and the core workflow needs no account or cloud service.

| Capability | What it does |
|---|---|
| Multi-plan workspace | Try alternatives in tabs and compare additions, removals, replacements, and conflicts |
| Multiple timetable views | Inspect one week, scan the semester, or compare two plans side by side |
| Registration order | Generate suggestions from remaining seats, pressure, and alternatives, then adjust manually |
| Course library | Manage semesters, periods, classifications, labels, courses, and meeting times |
| Import and export | Exchange library or plan JSON and export share text, PNG, or vector PDF |
| Local resilience | SQLite persistence, undo/redo, import preview, backup, and restore |
| Native Windows UI | WinUI 3, Mica, responsive panes, light/dark/system themes, and bilingual UI |

## Install

### Requirements

- Windows 10 version 1809 (build 17763) or later.
- An x64 device.
- Release packages are self-contained. End users do not need the .NET SDK, Visual Studio, or a separately installed Windows App SDK Runtime.

### Recommended: install from GitHub Releases

1. Open the [latest release](https://github.com/Gliese-876/Course-Planner/releases/latest).
2. Download `CoursePlanner-vVERSION-x64.zip` and `SHA256SUMS.txt`.
3. Optionally, but preferably, verify the archive with `Get-FileHash` and compare its SHA-256 value.
4. Extract the ZIP and double-click `Install-CoursePlanner.cmd`.

Alternatively, open Windows PowerShell in the extracted directory and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-CoursePlanner.ps1
```

The installer first verifies that the MSIX signer exactly matches the bundled certificate. On first install, it requests UAC approval to add the publisher certificate to Local Computer → Trusted People, then installs the app for the signed-in Windows user. Updates signed with the same certificate normally need no further elevation. Launch Course Planner from the Start menu afterward.

> [!IMPORTANT]
> Current GitHub builds use the project's self-signed sideload certificate. Download only from this repository's Releases page and verify the checksum before installation. A future Microsoft Store or trusted-code-signing distribution will remove the manual certificate-trust step.

For a manual install, add the release `.cer` to **Local Computer → Trusted People** from an administrator session, then double-click the `.msix`. To uninstall, open **Settings → Apps → Installed apps**, find Course Planner, and choose **Uninstall**.

## Quick start

1. Configure semester dates, the first day of the week, and periods.
2. Build classifications, labels, course offerings, and meeting times.
3. Create several plans and distribute candidate courses between them.
4. Use week, semester-overview, and comparison views to inspect trade-offs.
5. Review the registration-order recommendation and adjust it when needed.
6. Export a timetable, share text, course library, or plan.
7. Create backups in Settings. Use Backup and Restore—not exchange JSON—when moving the complete application state.

## JSON import guide

For bulk authoring, course-library sharing, or plan exchange, read the [complete JSON Import Guide](./docs/json-import-guide.en.md). It covers:

- the `courseLibrary` and `selectionPlan` package shapes;
- every field, numeric enum, date, and time format;
- `offeringId` calculation and verification;
- importer limits, preview behavior, common errors, and troubleshooting.

Start from files validated against the current importer:

- [Course-library example](./docs/examples/course-library.json)
- [Selection-plan example](./docs/examples/selection-plan.json)
- [Course-ID checker](./scripts/Get-CourseOfferingId.ps1)

## Build from source

Development requires Windows x64, [.NET SDK 10.0.301](./global.json) or a compatible newer feature band, and Windows Developer Mode. Visual Studio is optional.

```powershell
dotnet restore .\CoursePlannerWorkspace.slnx --locked-mode
dotnet build .\CoursePlannerWorkspace.slnx --configuration Debug --no-restore
dotnet run --project .\CoursePlanner\CoursePlanner.csproj --configuration Debug --runtime win-x64
```

Run the full verification suite before submitting changes:

```powershell
pwsh .\scripts\Test-Ci.ps1
```

### Release maintainers

Production releases use a local-build, cloud-signing flow. The local machine restores, builds, runs the full test suite, and packages an MSIX with a temporary signature. A GitHub workflow only verifies its SHA-256, replaces that signature with the stable publisher certificate, and creates the final installation assets; it does not build or test the app in the cloud.

- `COURSE_PLANNER_PFX_BASE64`: Base64 content of the encrypted PFX.
- `COURSE_PLANNER_PFX_PASSWORD`: the PFX password.
- Never commit a PFX, private key, password, temporary signing directory, or build output.
- Sign every update with the same publisher certificate; rotating it requires existing users to trust the new publisher again.
- The release machine also needs an authenticated GitHub CLI (`gh`) and WinApp CLI (`winapp`) on `PATH`.
- Create and push a `vMAJOR.MINOR.PATCH` tag from a clean release commit, then run `pwsh .\scripts\Stage-Release.ps1 -Tag vVERSION`. The script performs full local verification, builds the staged package with a one-time certificate, and dispatches the signing-only workflow; the stable private key is never downloaded locally.

## Contributing

Issues, documentation improvements, and code contributions are welcome. Add tests for new behavior, and check UI changes in light and dark themes and in both supported UI languages.

Published by [Gliese-876](https://github.com/Gliese-876) under the [MIT License](./LICENSE).
