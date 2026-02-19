# build.ps1 — Build, package, and create self-extracting exe
# Usage: pwsh build.ps1

$ErrorActionPreference = "Stop"

$repoRoot   = $PSScriptRoot
$projFile   = Join-Path $repoRoot "DxAggregator\DxAggregator.csproj"
$publishDir = Join-Path $repoRoot "publish\DxAggregator"
$outDir     = Join-Path $repoRoot "publish"
$zipFile    = Join-Path $outDir "DxAggregator-beta-win-x64.zip"
$exeFile    = Join-Path $outDir "DxAggregator-beta-win-x64.exe"
$sfxModule  = "C:\Program Files\7-Zip\7z.sfx"
$sevenZip   = "C:\Program Files\7-Zip\7z.exe"

# --- Step 1: dotnet publish ---
Write-Host "=== Publishing win-x64 self-contained build ===" -ForegroundColor Cyan
dotnet publish $projFile -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- Step 2: Create zip ---
Write-Host "=== Creating zip ===" -ForegroundColor Cyan
if (Test-Path $zipFile) { Remove-Item $zipFile }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipFile -Force
Write-Host "  -> $zipFile"

# --- Step 3: Create self-extracting exe ---
if (-not (Test-Path $sevenZip)) {
    Write-Warning "7-Zip not found at $sevenZip — skipping self-extracting exe"
} else {
    Write-Host "=== Creating self-extracting exe ===" -ForegroundColor Cyan

    $tempArchive = Join-Path $outDir "temp.7z"
    if (Test-Path $tempArchive) { Remove-Item $tempArchive }

    & $sevenZip a -r $tempArchive "$publishDir\*" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z archive creation failed" }

    $sfxConfig = @"
;!@Install@!UTF-8!
Title="DX Cluster Aggregator"
BeginPrompt="Extract DX Cluster Aggregator to a folder?"
DirectoryDefault="C:\DxAggregator"
;!@InstallEnd@!
"@
    $configFile = Join-Path $outDir "sfx_config.txt"
    [System.IO.File]::WriteAllText($configFile, $sfxConfig)

    # Concatenate: SFX module + config + archive = exe
    $sfxBytes    = [System.IO.File]::ReadAllBytes($sfxModule)
    $configBytes = [System.IO.File]::ReadAllBytes($configFile)
    $archBytes   = [System.IO.File]::ReadAllBytes($tempArchive)

    $stream = [System.IO.File]::Create($exeFile)
    $stream.Write($sfxBytes, 0, $sfxBytes.Length)
    $stream.Write($configBytes, 0, $configBytes.Length)
    $stream.Write($archBytes, 0, $archBytes.Length)
    $stream.Close()

    Remove-Item $tempArchive, $configFile
    Write-Host "  -> $exeFile"
}

# --- Summary ---
Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Get-ChildItem $zipFile, $exeFile -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
