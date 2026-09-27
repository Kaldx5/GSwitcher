# GSwitcher

**A portable Windows control dashboard built around a real laptop's power, thermal, and everyday workflow needs.**

Created by [Khaled Alrefai](https://github.com/Kaldx5). Designed around a Dell G16 environment, with a C# desktop host and an offline HTML/CSS/JavaScript interface.

## How it started 💻

My Dell G16 was running hotter and more aggressively than I expected during normal use. I started looking into what Windows was doing underneath, then manually tested power plans, processor limits, Boost behavior, and Dell thermal profiles.

The combinations I wanted to keep became PowerShell scripts. Those scripts grew into a menu, and that menu eventually became GSwitcher. The dashboard came later, after I had a workflow worth putting behind it. ⚙️

## What is inside

| Area | Implementation |
| --- | --- |
| Power modes | Discover installed plans, request a switch through `powercfg`, and verify the active mode. |
| Processor controls | Discover processor settings and validate AC/DC edits against Windows-reported values. |
| Thermal tools | Diagnostics, cooling profiles, and a bounded CPU calibration workload. |
| GPU visibility | Read-only dashboard route indicators; separate NVIDIA policy actions when supported. |
| Everyday automation | Optional startup, quick-switch OSD, idle and lid behavior, battery protection, internal-speaker muting, network focus, and telemetry HUD. |
| Interface | Offline WebView2 dashboard, light/dark themes, tray integration, and local settings. |

The source also includes Dell WMI and NVIDIA Control Panel route controllers. Their presence does **not** mean route switching is available through the dashboard or supported on every machine. See [operating boundaries](docs/OPERATING_BOUNDARIES.md).

## My part in the project 🛠️

I researched the system behavior, planned the controls, shaped the interface, and worked through the implementation and revisions. A lot of the progress came from trying a change on my own machine, watching what happened, and going back to adjust it.

I use AI and coding tools where they help with development and investigation. Choosing what belongs in the application, understanding the control paths, and reviewing the result remain part of my work.

Two decisions illustrate that approach:

- **Verify the resulting state:** the power interface reports the active Windows scheme after a switch, instead of treating a button press as success.
- **Separate observation from control:** dashboard GPU indicators are read-only; hardware-specific controllers and policy actions have separate availability checks.

## Architecture

```mermaid
flowchart LR
    UI[Offline dashboard] <-->|WebView2 messages| Host[WPF host and application state]
    Host --> Power[Windows power and processor settings]
    Host --> Hardware[GPU detection and thermal services]
    Host --> Automation[Optional background automation]
    Host --> Local[Local settings and runtime cleanup]
```

## Build from source

Windows x64, .NET Framework 4.8 development/build components, Windows PowerShell, and the Microsoft Edge WebView2 Runtime are required. The build script uses Windows Framework MSBuild and expects WPF build targets to be installed.

```powershell
.\scripts\restore_packages.ps1
.\build_portable.ps1
```

Run `dist\GSwitcher\GSwitcher.exe`. Dependency restoration requires internet access; the dashboard assets themselves are offline. The original package versions are pinned: WebView2 **1.0.2420.47**, NvAPIWrapper.Net **0.8.1.101**.

The builder replaces this checkout's generated `dist/GSwitcher` directory and preserves any existing settings there. Build from your own checkout rather than inside an installed copy.

## Checks

```powershell
.\scripts\verify_automation_pack.ps1
# Optional startup/exit check after building:
.\scripts\smoke_test.ps1
```

The first script checks source structure and expected implementation patterns; it is not a hardware integration test. The smoke script launches the app and checks startup, exit, and runtime cleanup. See [publication checks](docs/PUBLICATION_CHECKS.md) for the checks actually completed on this source snapshot.

## Repository layout

- `source/GSwitcher.Desktop/`: desktop host, services, models, and offline UI.
- `scripts/`: dependency restoration and selected verification tools.
- `docs/`: operating boundaries, dependency attribution, and publication notes.
- `build_portable.ps1`: portable build; `build_pro.ps1`: compatibility wrapper.

This repository publishes the application source, not the original development archive. Agent conversations, machine diagnostics, personal settings, nested copies, and prebuilt binaries are excluded.

## Author

[GitHub](https://github.com/Kaldx5) · [LinkedIn](https://www.linkedin.com/in/khaled-alrefai-668079273/)

Source is published for inspection and portfolio presentation. No open-source license is granted for original project code at this stage; third-party components retain their own licenses. See [third-party notices](docs/THIRD_PARTY_NOTICES.md).
