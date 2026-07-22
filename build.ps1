#requires -Version 5.1
<#
WKI Clipper - build script
  - publishes the app self-contained (single .exe, ~75 MB)
  - bundles ffmpeg.exe (gyan full build, ~214 MB) for AMD/NVIDIA/Intel encoders
  - compiles installer.iss with Inno Setup
#>

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "== WKI Clipper Build ==" -ForegroundColor Cyan

$pub = Join-Path $root 'publish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

Write-Host "`n[1/4] dotnet publish (self-contained single-file)..." -ForegroundColor Yellow
dotnet publish (Join-Path $root 'WKI_Clipper\WKI_Clipper.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:EnableCompressionInSingleFile=true `
    -o $pub | Out-Null

$exePath = Join-Path $pub 'WKI_Clipper.exe'
if (-not (Test-Path $exePath)) {
    throw ("Publish failed - WKI_Clipper.exe not found in " + $pub)
}

Write-Host "`n[2/4] Bundling FFmpeg..." -ForegroundColor Yellow
$ffSrc = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
if (-not $ffSrc) {
    $candidates = Get-ChildItem `
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_*\ffmpeg-*-full_build\bin\ffmpeg.exe" `
        -ErrorAction SilentlyContinue
    if ($candidates) { $ffSrc = $candidates[0].FullName }
}
if (-not $ffSrc) {
    throw "ffmpeg.exe not found. Install it: winget install Gyan.FFmpeg"
}
$ffDst = Join-Path $pub 'Assets\ffmpeg'
New-Item -ItemType Directory -Path $ffDst -Force | Out-Null
Copy-Item $ffSrc $ffDst -Force
$ffSize = "{0:N1} MB" -f ((Get-Item (Join-Path $ffDst 'ffmpeg.exe')).Length / 1MB)
Write-Host ("  ffmpeg.exe ($ffSize) bundled from " + $ffSrc)

Write-Host "`n[3/4] Compiling Inno Setup..." -ForegroundColor Yellow
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    throw ("Inno Setup 6 not found at " + $iscc)
}
& $iscc (Join-Path $root 'installer.iss') /Q
if ($LASTEXITCODE -ne 0) { throw ("ISCC failed with exit " + $LASTEXITCODE) }

Write-Host "`n[4/4] Done." -ForegroundColor Green
$out = Get-ChildItem (Join-Path $root 'installer_output\*.exe') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$outSize = "{0:N1} MB" -f ($out.Length / 1MB)
Write-Host ("`nSetup.exe: " + $out.FullName + " (" + $outSize + ")") -ForegroundColor Cyan
