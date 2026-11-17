# Builds HAK-Setup-<version>.exe: one self-extracting installer (Windows IExpress, built into Windows — nothing to download,
# no administrator rights to build or to install). It unpacks to a temporary folder and runs install.ps1 (per-user install).
# Usage (repository root):  .\tools\package\build-setup.ps1 [-Package <stand-alone package folder>] [-Version 2026.11.17] [-Output artifacts]
param([string]$Package, [string]$Version = (Get-Date -Format "yyyy.MM.dd"), [string]$Output = "artifacts")

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
New-Item -ItemType Directory -Force $outDir | Out-Null
$work = Join-Path $env:TEMP ("hak-setup-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $work | Out-Null
try {
    if (-not $Package) {
        $Package = Join-Path $work "pkg"
        & (Join-Path $PSScriptRoot "package.ps1") -Output $Package
    }
    $Package = (Resolve-Path $Package).Path
    if (-not (Test-Path (Join-Path $Package "HAK.exe"))) { throw "Không thấy HAK.exe trong $Package" }

    Write-Host "Đóng gói phiên bản $Version"
    $content = Join-Path $work "content"
    Copy-Item $Package $content -Recurse
    Set-Content (Join-Path $content "VERSION.txt") $Version -Encoding ascii
    $stage = Join-Path $work "stage"
    New-Item -ItemType Directory $stage | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($content, (Join-Path $stage "payload.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item (Join-Path $PSScriptRoot "install.ps1"), (Join-Path $PSScriptRoot "uninstall.ps1") $stage
    # cmd.exe reads .cmd files in the OEM code page: ASCII only, CRLF.
    [IO.File]::WriteAllText((Join-Path $stage "setup.cmd"),
        "@echo off`r`ntitle Cai dat HAK Accounting`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`" -Payload `"%~dp0payload.zip`"`r`n",
        [Text.Encoding]::ASCII)

    $target = Join-Path $outDir "HAK-Setup-$Version.exe"
    if (Test-Path $target) { Remove-Item $target }
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$target
FriendlyName=HAK Accounting $Version
AppLaunched=cmd /c setup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="payload.zip"
FILE1="install.ps1"
FILE2="uninstall.ps1"
FILE3="setup.cmd"
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
    $sedPath = Join-Path $work "hak.sed"
    [IO.File]::WriteAllText($sedPath, $sed, [Text.Encoding]::ASCII)
    Write-Host "Tạo bộ cài bằng IExpress..."
    $p = Start-Process (Join-Path $env:WINDIR "System32\iexpress.exe") -ArgumentList "/N", "/Q", $sedPath -Wait -PassThru
    if (-not (Test-Path $target)) { throw "IExpress không tạo được $target (mã thoát $($p.ExitCode))." }
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content "$target.sha256.txt" "$hash  $(Split-Path $target -Leaf)" -Encoding ascii
    Write-Host ("Xong: {0} ({1:N0} MB)" -f $target, ((Get-Item $target).Length / 1MB))
    Write-Host "SHA-256: $hash"
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
