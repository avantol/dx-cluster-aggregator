# Building & Packaging DX Cluster Aggregator

## Prerequisites

- .NET 6.0 SDK
- PowerShell 7+ (`pwsh`)
- 7-Zip installed at `C:\Program Files\7-Zip\` (needed for self-extracting exe only)

## One-command build

```powershell
pwsh build.ps1
```

This runs three steps:

1. **dotnet publish** — builds a self-contained win-x64 release to `publish/DxAggregator/`
2. **Zip** — creates `publish/DxAggregator-beta-win-x64.zip` using PowerShell `Compress-Archive`
3. **Self-extracting exe** — creates `publish/DxAggregator-beta-win-x64.exe` using the 7-Zip SFX module (skipped if 7-Zip is not installed)

## Output

| File | Description |
|------|-------------|
| `publish/DxAggregator/` | Full self-contained build directory |
| `publish/DxAggregator-beta-win-x64.zip` | Zip archive for users who can extract zips |
| `publish/DxAggregator-beta-win-x64.exe` | Self-extracting exe (default extract path: `C:\DxAggregator`) |

The `publish/` directory is in `.gitignore` and not tracked by git.

## End-user instructions

### Self-extracting exe (recommended)

1. Run `DxAggregator-beta-win-x64.exe`
2. Choose an extraction folder (default: `C:\DxAggregator`)
3. Run `DxAggregator.exe` from the extracted folder
4. Open a browser to `http://localhost:5050`

### Zip

1. Extract `DxAggregator-beta-win-x64.zip` to a folder
2. Run `DxAggregator.exe`
3. Open a browser to `http://localhost:5050`
