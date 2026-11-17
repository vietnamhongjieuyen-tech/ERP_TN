# HAK Accounting — gỡ chương trình. DỮ LIỆU KHÔNG BỊ XÓA (%LOCALAPPDATA%\HAK): sổ sách, bản sao lưu, tệp đính kèm vẫn còn.
param([switch]$Quiet, [switch]$NoShortcuts, [switch]$NoRegistry)
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot
Write-Host "Gỡ HAK Accounting khỏi $dir"
Write-Host "Dữ liệu ở $env:LOCALAPPDATA\HAK được GIỮ NGUYÊN (xóa tay nếu thật sự không cần, sau khi đã sao lưu)."
if (-not $Quiet) {
    $a = Read-Host "Gõ G rồi Enter để gỡ, Enter để hủy"
    if ($a -notmatch '^[gG]$') { Write-Host "Đã hủy."; exit 0 }
}
$exe = Join-Path $dir "HAK.exe"
Get-Process HAK -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) } | Stop-Process -Force
Start-Sleep -Seconds 1
if (-not $NoShortcuts) {
    Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "HAK Accounting") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "HAK Accounting.lnk") -Force -ErrorAction SilentlyContinue
}
if (-not $NoRegistry) { Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HAKAccounting" -Recurse -Force -ErrorAction SilentlyContinue }
# This script runs from the folder being removed: a separate process deletes it after this one exits.
Start-Process cmd.exe -ArgumentList "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q `"$dir`"" -WindowStyle Hidden
Write-Host "Đã gỡ HAK Accounting."
if (-not $Quiet) { Start-Sleep -Seconds 3 }
