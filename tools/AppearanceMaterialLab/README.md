# Appearance Material Lab

GPU-backed prototypes for the `LiquidGlass_GPU` and `CyberPunk_GPU` themes.

The lab is intentionally outside `LiteMonitor.csproj`; the main project excludes
`tools/**` so prototype entry points and dependencies cannot leak into the
production executable.

## Build

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/AppearanceMaterialLab/build-shaders.ps1
dotnet build tools/AppearanceMaterialLab/AppearanceMaterialLab.csproj -c Release --nologo
```

## Run

```powershell
dotnet run --project tools/AppearanceMaterialLab/AppearanceMaterialLab.csproj -c Release -- --mode glass
dotnet run --project tools/AppearanceMaterialLab/AppearanceMaterialLab.csproj -c Release -- --mode cyber
```

The fixed fixture is deterministic. Live capture is opt-in with `--live` and
uses a dedicated demo backdrop; it never writes captured desktop pixels.

Keys:

- `1`: Liquid Glass
- `2`: Cyberpunk
- `L`: live capture / fixed fixture
- `E`: toggle `WDA_EXCLUDEFROMCAPTURE`
- `C`: save the rendered backbuffer
- `Esc`: close
