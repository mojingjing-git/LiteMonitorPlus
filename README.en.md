> [!NOTE]
> **This project is derived from and modified based on [LiteMonitor](https://github.com/Diorser/LiteMonitor) (original author Diorser, MIT License).**
> Upstream project home: https://litemonitor.cn ｜ This is an unofficial version, not affiliated with the original author.
> Full license text: [LICENSE](./LICENSE) · Capability differences from upstream: [NOTICE](./NOTICE) · Architecture documentation: [docs/](./docs/).

[中文文档](./README.md)

# <img src="./resources/screenshots/logo.png"  width="28" style="vertical-align: middle; margin-top: -4px;" /> LiteMonitor++
A lightweight, customizable open-source desktop hardware monitoring tool — real-time monitoring of CPU, GPU, memory, disk, network, and other system performance.

A lightweight and customizable desktop hardware monitoring tool — real-time monitoring of system performance such as CPU, GPU, memory, disk, and network.

Supports horizontal / vertical / taskbar / web display, theme switching, multiple languages, opacity display, three-color alerts, and more — with a clean and highly configurable interface.
> **Download the latest version now:** [GitHub Releases → LiteMonitor++ Latest](https://github.com/mojingjing-git/LiteMonitorPlus/releases/latest)

> Memory cleanup and custom plugins are already supported. See: [Plugin development guide (no programming required)](./resources/plugins/PLUGIN_DEV_GUIDE.md)


![LiteMonitor++ main interface](./resources/screenshots/overview.png)

###  Horizontal mode / Taskbar display mode
![LiteMonitor++ horizontal / taskbar display](./resources/screenshots/overview3.png)


# System Monitoring Features

| Category | Monitored Metrics |
|------|-----------|
| **Processor (CPU)**  | Real-time monitoring of CPU usage, temperature, frequency, power consumption, fan speed, water cooling, and more. |
| **Graphics (GPU)**  | GPU usage, core temperature, VRAM, frequency, power consumption and fan speed; compatible with NVIDIA / AMD / Intel GPUs. |
| **Host (HOST)** | System memory usage, FPS refresh rate, disk temperature, motherboard temperature, case fans, total system power consumption, and more — a clear view of your whole system. |
| **Battery (Battery)** | Battery status (charging state, level, power consumption, current, voltage, etc.). |
| **Disk (Disk)**   | Disk read and write speeds (KB/s, MB/s), helping you analyze storage I/O activity. Automatic / manual disk selection supported. |
| **Network (Network)** | Real-time upload and download speeds (KB/s, MB/s), providing lightweight network traffic monitoring. Automatic / manual NIC selection supported. |
| **Traffic (Traffic)** | Daily upload and download totals, helping you analyze your network usage habits. |
| **Plugin (Plugin)** | Monitors weather, stocks, cryptocurrencies, proxy latency, exchange rates, and more; supports custom plugins to extend monitoring items and functionality. |
| **Sensors (Sensors)** | The hardware details panel shows and monitors all system hardware sensor data. |


> LiteMonitor++ is under active development. If you need more monitoring items or feature support, please share your suggestions in [GitHub Issues](https://github.com/mojingjing-git/LiteMonitorPlus/issues)!

---

###  Network speed test
![LiteMonitor++ network speed test](./resources/screenshots/overview4.png)  ![LiteMonitor++ menu](./resources/screenshots/overview5.jpg)


###  Monitoring history
![LiteMonitor++ monitoring history](./resources/screenshots/overview9.png)

###  Historical traffic statistics
![LiteMonitor++ theme editor](./resources/screenshots/overview7.png)

###  Web monitor
![LiteMonitor++ web monitor](./resources/screenshots/web.png)

---

# Product Features

| Feature | Description |
|---|---|
| Custom themes | Define colors, fonts, spacing, corner radius, and more through JSON; themes are extensible and reusable. Theme system v2 is easier to maintain. |
| **Theme Studio** | Visually edit every theme property: color scheme, material parameters, layout, corner radius, refraction and blur strength — with live preview and theme package export. |
| **Theme Market** | Browse a remote theme index and install or uninstall themes with one click; theme package integrity is validated before installation. |
| **Three-color alerts** | Monitoring items automatically switch the color of their progress bar / value based on thresholds, with customizable colors and independent thresholds for network / disk. |
| **Rule-based alerts** | Besides the three-color thresholds, alerts can be defined by combining a "condition + duration + action"; actions include popups, sounds, and writing to the alert log. |
| **Scenario modes** | Save a group of settings as a configuration profile, pre-check it and then apply it as a whole; ideal for one-click switching between "gaming", "office", and "power saving" scenarios. |
| **Diagnostics Center** | Records runtime events and can export a diagnostic package (configuration, hardware info, event logs, screenshots) to make troubleshooting easier. |
| **Trend statistics** | Metrics such as temperature, load, power consumption, frequency, and FPS are bucketed per second for short-term trends and written to history files per day, so long-term curves can be reviewed. |
| Multilingual UI | Multiple languages are built in; all menus, short labels, and monitoring items are internationalized on the fly. |
| Monitoring item management | Show or hide the CPU, GPU, VRAM, memory, disk, and network modules as needed. |
| **Multi-GPU monitoring** | Monitor several independent GPUs at the same time; temperature, usage, power consumption, and VRAM metrics are shown per card and grouped by card. |
| **Horizontal mode** | A brand-new horizontal bar layout with independent width per column, smart unit formatting, two-row display, and automatic panel width calculation. |
| Panel width adjustment | Adjust the panel width instantly; the layout reflows automatically. |
| **UI scaling** | Adaptive DPI plus user-defined scaling; the interface and fonts scale proportionally. |
| **Smooth animations** | Value updates are animated smoothly, reducing the jumpiness of sudden changes; the speed is adjustable. |
| Window and interface | Rounded corners, opacity adjustment, shadows, and high-quality font rendering for a clean and elegant look. |
| Auto-hide near screen edges | Automatically collapses toward the screen edge and pops back out as you approach it, with correct detection across multiple monitors. |
| **Clamp to screen** | When the option is enabled, the window cannot be dragged outside the visible screen area. |
| Mouse click-through mode | Once enabled, the window does not intercept mouse events, so the apps behind it can be operated directly. |
| Instant UI and theme switching | Switching a theme or language refreshes the interface immediately, with no restart needed. |
| Smart value formatting | Units and decimal places are formatted automatically; horizontal mode supports smart "/s" removal, rounding for values >= 100, and more. |
| Automatic update check | Checks for new versions silently on startup and shows a popup for manual checks. Both a domestic mirror and GitHub are supported as sources. |
| Auto-start on boot | Implemented through a scheduled task, providing administrator-level auto-start. |
| Configuration file storage | All settings are written to `settings.json` in real time, with migration and backup support. |


## GPU Material Themes and Live Desktop Background

Some themes declare `rendering.backend = "gpu"` in their JSON and render the panel material directly through DirectX 11 (DXGI / D3D11) instead of the traditional GDI+ drawing. **Both** the theme declaration **and** the user switch must be satisfied to enter the GPU route; if either one is missing, the GDI+ appearance is kept.

### The three rendering routes

| Route | Preconditions | Result |
|---|---|---|
| **GDI+ route** (enum `Legacy`, automatic fallback) | The theme does not declare `rendering.backend = "gpu"`, the GPU does not support DirectX 11, or the app was started with `--safe` | Traditional GDI+ drawing; works in any environment |
| **GPU fixed background** (enum `GpuFixedInput`) | GPU theme + "GPU material rendering" enabled | Real glass material (refraction / thickness / highlights) over a procedural background |
| **GPU live desktop background** (enum `GpuLiveBackground`) | Everything above + "Live desktop background" enabled + the WDA policy accepted | A **live blurred desktop** behind the glass; verified to work on layered windows too (opacity < 100%) |

Settings → **Main panel**: the GPU route switches ("material rendering" / "live desktop background") sit right below the theme selector, and the route status line above them shows the theme's declared route and the route actually in effect in real time; the dedicated **"Rendering route"** page carries the full diagnostics (including the real reason codes, such as `safe_mode` / `gpu_effects_opt_in_required`). Theme entries in the theme drop-down and in the tray menu are labelled ` (GPU material)` / ` (GDI+)`.

### Support matrix

| Item | Requirement | When not met |
|---|---|---|
| Operating system | Windows 10 / 11 | Not supported |
| Runtime | .NET 8 desktop runtime (the release package is framework-dependent) | Cannot start |
| GPU | Provides a DXGI adapter and supports DirectX 11 | Automatically falls back to GDI+, the panel still displays normally |
| Theme | The theme JSON declares `rendering.backend = "gpu"` | That theme keeps its GDI+ appearance |
| User switch | Settings → Main panel → "GPU material rendering" | Without it the GPU route is not entered |
| Multi-monitor / mixed DPI | Supported; the panel can be dragged to a secondary display (including a negative coordinate origin) | — |
| Live desktop background | Requires DXGI Desktop Duplication and WDA support | On capture failure a readable fixed material is kept |

### WDA (window capture exclusion) behavior

With "Live desktop background" enabled, the app uses `WDA_EXCLUDEFROMCAPTURE` to exclude the **entire top-level window** from supported screen capture:

- Supported screenshot and screen-recording tools **do not show LiteMonitor++**; that area shows the background itself
- The GDI+ route and **fixed-input mode** (live background not enabled) are unaffected
- The **CyberPunk** theme always performs **zero desktop capture** and never requests it
- The first enable shows a "Live Glass Capture Notice" confirmation; declining keeps it off

### Fallback behavior

- Without the "GPU material rendering" switch, a theme is not activated even if it declares GPU
- If the GPU does not support DXGI or the device fails to be created → falls back to the GDI+ route
- If live background capture fails → the **window-level capture exclusion is withdrawn** (the panel shows up in screenshots / recordings again) while the readable fixed GPU material is kept, so no "hole" is left behind
- After a failed GPU start the app does not retry automatically; re-applying the settings once is required to try again
- The `--safe` / `--safe-mode` / `--disable-gpu` launch arguments force the GDI+ route

---

## Installation and Usage

### Prerequisites

| Item | Requirement | Notes |
|---|---|---|
| Operating system | Windows 10 / 11 | — |
| Runtime | .NET 8 desktop runtime (Desktop Runtime) | The release package is framework-dependent; without the runtime installed it cannot start |
| Permissions | Administrator rights | The program manifest declares `requireAdministrator`; used to install the PawnIO driver, write to the program directory, and register the auto-start scheduled task |
| Driver | PawnIO | The official installer `resources/assets/PawnIO_setup.exe` ships with the package; used to read sensor data that standard APIs such as MSR / SuperIO / SMBus / PCI registers cannot access |

### Installation steps

1. Go to the [Releases page](https://github.com/mojingjing-git/LiteMonitorPlus/releases) and download the latest archive
2. Extract it and run `LiteMonitor++.exe`
3. The app automatically loads the matching language file based on the system language

---

## Plugin Market

Settings → Plugin Market lets you install plugins from the community plugin repository with one click.

| Item | Details |
|---|---|
| Plugin source | Fixed to [awesome-litemonitor-plugins](https://github.com/mojingjing-git/awesome-litemonitor-plugins); the app provides no way to change it |
| Install location | `resources/plugins/` under the program directory, alongside the built-in plugins |
| Uninstall | Click "Uninstall" on the market page; only plugins installed through the market are removed — built-in plugins can never be uninstalled |
| Integrity check | Every plugin carries a SHA-256 in the index; the raw bytes of the download are verified on install and a mismatch aborts the installation |

Installed plugins are **purely declarative JSON configuration** (they only declare *where to fetch data, how to extract it, and how to display it*) and **cannot execute third-party scripts or code**. They do make network requests according to their configuration, which may send the API keys you entered to the endpoints they declare — check the request domains listed in each plugin's README before installing.

> Writing to the program directory requires permission. If the program is installed under `C:\Program Files\`, installation will fail — run the app as administrator.

Want to write your own plugin? See [`resources/plugins/PLUGIN_DEV_GUIDE.md`](./resources/plugins/PLUGIN_DEV_GUIDE.md).

---

## Theme System

Theme files live in the `resources/themes/` directory. There are currently 20 built-in themes, 6 of which are GPU material themes.

Example:
```json
{
  "name": "DarkFlat_Classic",
  "layout": { "rowHeight": 40, "cornerRadius": 10 },
  "color": {
    "background": "#202225",
    "textPrimary": "#EAEAEA",
    "barLow": "#00C853"
  }
}
```

> New in theme system v2:
> - Leaner, unified layout fields
> - Fonts and layout scale independently
> - All layout is handled automatically by Theme.Scale
> - Easier to build your own theme templates

GPU material themes additionally declare `rendering.backend = "gpu"` and `rendering.parameters` (refraction, thickness, blur radius, glass style, etc.) in their JSON. See [docs/architecture/rendering.md](docs/architecture/rendering.md) for the parameter ranges and fallback rules.

---

## Settings File (settings.json)

The configuration file is written as `settings.json` in the **program directory** (portable layout, next to the executable). All persisted items are defined by the writable properties of `src/Core/Settings.cs`, 106 in total; the table below groups the common ones by meaning, and the remaining ones can keep their defaults.

| Group | Common fields | Description |
|---|---|---|
| Appearance | `Skin` | Current theme name |
| Appearance | `UIScale` | User interface scaling factor |
| Appearance | `Opacity` | Opacity (0.1 ~ 1.0) |
| Appearance | `HorizontalMode` | Horizontal / vertical display mode |
| Appearance | `PanelWidth` | Panel width (shared by both modes) |
| Appearance | `AnimationSpeed` | Value smoothing animation speed |
| Appearance | `CornerRadius` / `ShadowEnabled` / `TransparentEnabled` | Rounded corners, shadows, and transparent window switches |
| Interaction | `TopMost` | Whether the window stays on top |
| Interaction | `AutoHide` | Whether auto-hide near screen edges is enabled |
| Interaction | `ClampToScreen` | Keep the window on screen after dragging |
| Interaction | `ClickThrough` | Whether mouse click-through is enabled |
| Interaction | `MonitorItems` | Per-item switches, ordering, group titles, and custom upper / lower limits (`MonitorItemConfig`) |
| Collection | `RefreshMs` | Refresh interval (full presets supported) |
| Collection | `PreferredNetwork` / `PreferredDisk` | Manual NIC / disk selection (empty = auto) |
| Collection | `DiskProbeMode` | Disk probing level (off / counters only / full) |
| Collection | `LowPowerMode` | Low power mode (off / auto / power saving) |
| Alerts | `AlertRules` | Rule-based alert rule list |
| Alerts | `NetworkThreshold` / `DiskThreshold` | Independent thresholds for network and disk |
| Alerts | `AlertSoundEnabled` / `AlertPopupEnabled` | Alert sound and popup switches |
| Scenarios | `ScenarioProfiles` | Scenario mode configuration profile list |
| Trends | `TrendEnabled` / `TrendRetentionDays` | Trend statistics switch and history retention days |
| Plugins | `InstalledPlugins` / `PluginSettings` | Installed plugins and each plugin's private configuration |
| Security | `WebServerEnabled` / `WebServerBindMode` / `WebServerPassword` | Web panel switch, bind scope, and access password |
| Security | `UpdateChannel` / `AutoCheckUpdate` | Update channel and automatic check switch |
| System | `Language` | Current language (auto-detected or switched manually) |
| System | `AutoStart` | Whether to start on boot |
| System | `Theme` / `WebPort` etc. | All remaining items can be changed in the settings UI and are written to disk in real time |

> Passwords and plugin secrets are never stored in plain text: passwords are protected with DPAPI (CurrentUser), configuration writes use a "temporary file + atomic replace", and a failed write never leaves plain text on disk.

---

## Architecture Overview

The source code lives under `src/`. `src/Contracts`, `src/HardwareHost`, and `src/Cli` are separate projects; all other directories are compiled into the main assembly `LiteMonitor++`; `LiteMonitor.Updater/` sits at the repository root.

| Module | Responsibility |
|------|------|
| `System/Program.cs` | Entry point: single-instance mutex, global exception capture, DPI initialization, driver pre-check |
| `UI/MainForm_Transparent.cs` | Main form, dragging, tray menu, auto-hide, opacity, position persistence |
| `UI/UIController.cs` | Theme loading, DPI/UIScale scaling, layout rebuild, rendering entry, timed refresh |
| `UI/UIRenderer.cs` | Vertical renderer (groups, progress bars, title rendering) |
| `UI/HorizontalRenderer.cs` | Horizontal renderer (two-row layout, smart labels and values) |
| `UI/UILayout.cs` / `UI/HorizontalLayout.cs` | Dynamic vertical layout calculation / horizontal column width and total panel width calculation |
| `UI/Decor/` | GDI+ decoration layer drawing (frame, progress bars, geometry, texture cache) |
| `UI/Settings/` | Settings panel pages (main panel, rendering route, taskbar, monitor items, thresholds, system & hardware, plugins, plugin market, theme market, scenarios, diagnostics) |
| `UI/LiveGlassController.cs` | Live desktop capture and presentation control for the GPU route |
| `UI/RenderingTransitionCoordinator.cs` | State machine and resource handover for GPU ↔ GDI+ route switching |
| `Core/Actions/` | Application action aggregation: program actions, settings switches, web-side actions |
| `Core/Alerts/` | Rule-based alerts: model, evaluator, runtime, and legacy threshold migration |
| `Core/Scenarios/` | Scenario modes: configuration profiles, pre-check, apply coordination, and projection |
| `Core/Trends/` | Trend data: short-term buffer, history storage (V2), and view metrics |
| `Core/ThemeManager.cs` | Theme loading, color parsing, font building |
| `Core/LanguageManager.cs` | Multi-language loading, flattened key access |
| `Core/Settings.cs` | `settings.json` configuration model; migration, encryption, and persistence of passwords and plugin secrets |
| `Core/MetricItem.cs` / `Core/MetricUtils.cs` | Monitoring item model, value formatting, three-color threshold evaluation, progress bar ratio |
| `System/HardwareMonitor.cs` | Collects CPU/GPU/MEM/NET/DISK information; automatic / manual device selection |
| `System/HardwareServices/` | Sampling coordinator (single ≤1 Hz entry point), sensor mapping and device routing, VRAM capacity, CPU voltage, committed memory, derived metrics (total system power), driver installation |
| `System/NetworkTraffic/` | ETW network traffic capture, zero-copy decoding, traffic exclusion by remote IP |
| `System/Pipe/` | Named pipe channel and supervisor for the hardware sampling subprocess |
| `System/WebServer/` | Web monitoring service: HTTP/1.1 + WebSocket, session and password protocol, theme CSS mapping |
| `System/UpdateChecker.cs` | GitHub + domestic mirror version detection, update metadata signature verification |
| `System/AutoStart.cs` | Manages scheduled tasks to implement auto-start on boot |
| `System/InfoService/` | System information queries (battery, system summary, etc.) |
| `System/Backdrop/` | Desktop background capture and blur chain for the GDI+ route |
| `Rendering/` | GPU rendering route: contracts, configuration and route decisions, device and resource ledger, geometry, materials, scene, presentation, desktop duplication capture |
| `Rendering/Capture/` | Live desktop frame source for the GPU route |
| `Rendering/Power/` | Total system power derivation |
| `Plugins/` | Plugin model, network sandbox (method set / host allow-list / redirect re-check / concurrency cap / SSRF address policies), executor, market install and uninstall transactions |
| `ThemeEditor/` | Theme Studio: property editing, preview, and theme file read/write service |
| `Diagnostics/` | Diagnostics center: event store, diagnostic hub, and diagnostic package collection (incl. extended providers) |
| `Contracts/` | Cross-process contract library: IPC encoding/decoding, message types, sampling model (separate project) |
| `HardwareHost/` | Hardware sampling subprocess: minimal sampling implementation and pipe session (separate project) |
| `Cli/` | Command-line tooling: theme package validation (shares `ThemeMarketValidator` sources with the main app) (separate project) |
| `LiteMonitor.Updater/` | Standalone updater: digest verification, transactional replacement, and rollback on failure |

> ⚠️ **Two backdrop capture chains coexist**: `System/Backdrop/` (GDI+ route) and `Rendering/Capture/` (GPU route) each implement their own frame source, WDA exclusion, and cleanup logic, and share nothing with each other.
> Before changing capture-related code, first confirm which chain you are on.

Layer responsibilities, dependency direction, and contracts are detailed in [`docs/architecture/`](docs/architecture/):

| Document | Content |
|---|---|
| [overview.md](docs/architecture/overview.md) | Assembly and process topology, layered dependency graph, cross-process data flow |
| [layering.md](docs/architecture/layering.md) | Responsibility boundaries and dependency constraints of each layer |
| [process-isolation.md](docs/architecture/process-isolation.md) | Hardware sampling process isolation and pipe protocol |
| [rendering.md](docs/architecture/rendering.md) | The three rendering routes, the GPU rendering stack, and material parameters |
| [rendering-transition.md](docs/architecture/rendering-transition.md) | The GPU ↔ GDI+ switching contract and error codes |
| [plugin-sandbox.md](docs/architecture/plugin-sandbox.md) | The plugin sandbox and network access gates |
| [market.md](docs/architecture/market.md) | Trust boundaries and install transactions of the plugin market and theme market |
| [known-gaps.md](docs/architecture/known-gaps.md) | Known gaps and limitations |

---

## Build Instructions

### Requirements
- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 or Rider

### Build
```bash
git clone https://github.com/mojingjing-git/LiteMonitorPlus.git
cd LiteMonitorPlus
dotnet build -c Release
```

Output:
```
/bin/Release/net8.0-windows/LiteMonitor++.exe
```

---

## Open Source License

This project is released under the **MIT License** and may be freely used, modified, and redistributed.

- Full license text: [LICENSE](./LICENSE)
- Upstream project and capability differences: [NOTICE](./NOTICE)
- Upstream project: https://github.com/Diorser/LiteMonitor (original author Diorser)

---

## Contact
**Project author**: mojingjing-git
**Project home**: [https://github.com/mojingjing-git/LiteMonitorPlus](https://github.com/mojingjing-git/LiteMonitorPlus)

**Upstream project (original author Diorser, MIT License)**: [https://github.com/Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)
