# HAK Accounting — cài đặt / nâng cấp cho người dùng hiện tại (không cần quyền quản trị).
# Chương trình: %LOCALAPPDATA%\Programs\HAK ; dữ liệu: %LOCALAPPDATA%\HAK\<Production|Test> (không bao giờ bị xóa / ghi đè).
# Gọi từ bộ cài (setup.cmd):  install.ps1 -Payload payload.zip
param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\HAK"),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA "HAK"),
    [switch]$NoShortcuts,      # dùng khi thử bộ cài
    [switch]$NoRegistry,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
function Say($m) { Write-Host $m }
function Fail($m) { Write-Host ""; Write-Host "LỖI: $m" -ForegroundColor Red; if (-not $Quiet) { Read-Host "Nhấn Enter để đóng" | Out-Null }; exit 1 }

try {
    $Payload = (Resolve-Path $Payload).Path
    $version = "?"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Payload)
    try {
        $entry = $zip.GetEntry("VERSION.txt")
        if ($entry) { $r = New-Object IO.StreamReader($entry.Open()); $version = $r.ReadToEnd().Trim(); $r.Close() }
    } finally { $zip.Dispose() }
    Say "Cài đặt HAK Accounting phiên bản $version"
    Say "  Chương trình: $InstallDir"
    Say "  Dữ liệu (giữ nguyên): $DataRoot"
    Say ""

    # 1. HAK đang chạy từ thư mục cài đặt thì phải tắt trước (tránh khóa tệp và ghi dở dữ liệu).
    $exe = Join-Path $InstallDir "HAK.exe"
    $running = @(Get-Process HAK -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) })
    if ($running.Count -gt 0) {
        if ($Quiet) { Fail "HAK đang chạy. Tắt HAK rồi chạy lại bộ cài." }
        Say "HAK đang chạy. Bộ cài cần tắt HAK (người khác đang nhập liệu trên máy này sẽ bị ngắt)."
        $a = Read-Host "Gõ C rồi Enter để tắt HAK và tiếp tục, Enter để hủy"
        if ($a -notmatch '^[cC]$') { Fail "Đã hủy cài đặt." }
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    # 2. Nâng cấp: chép CSDL hiện có trước khi phiên bản mới nâng cấp cấu trúc dữ liệu lúc khởi động.
    $old = if (Test-Path (Join-Path $InstallDir "VERSION.txt")) { (Get-Content (Join-Path $InstallDir "VERSION.txt") -Raw).Trim() } else { $null }
    foreach ($envName in "Production", "Test") {
        $db = Join-Path $DataRoot "$envName\hak.db"
        if ((Test-Path $db) -and $old) {
            $dest = Join-Path $DataRoot "$envName\backups"
            New-Item -ItemType Directory -Force $dest | Out-Null
            $copy = Join-Path $dest ("truoc-nang-cap-{0}-{1}.db" -f ($old -replace '[^\w.-]', '_'), (Get-Date -Format "yyyyMMdd-HHmmss"))
            Copy-Item $db $copy
            foreach ($side in "-wal", "-shm") { if (Test-Path "$db$side") { Copy-Item "$db$side" "$copy$side" } }
            Say "Đã chép dữ liệu $envName trước khi nâng cấp: $copy"
        }
    }

    # 3. Giải nén vào thư mục mới rồi tráo, để lỗi giữa chừng không làm hỏng bản đang dùng.
    $parent = Split-Path $InstallDir -Parent
    New-Item -ItemType Directory -Force $parent | Out-Null
    $stage = "$InstallDir.new"
    $prev = "$InstallDir.old"
    foreach ($d in $stage, $prev) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    Say "Đang giải nén..."
    [IO.Compression.ZipFile]::ExtractToDirectory($Payload, $stage)
    $sums = Join-Path $stage "SHA256SUMS.txt"
    if (Test-Path $sums) {
        $bad = 0
        foreach ($line in Get-Content $sums -Encoding UTF8) {
            if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { continue }
            $f = Join-Path $stage $Matches[2]
            if (-not (Test-Path $f) -or (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches[1]) { $bad++ }
        }
        if ($bad -gt 0) { Remove-Item $stage -Recurse -Force; Fail "$bad tệp không đúng mã băm — bộ cài bị hỏng, tải lại bộ cài." }
    }
    Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $stage
    if (Test-Path $InstallDir) { Rename-Item $InstallDir (Split-Path $prev -Leaf) }
    try { Rename-Item $stage (Split-Path $InstallDir -Leaf) }
    catch { if (Test-Path $prev) { Rename-Item $prev (Split-Path $InstallDir -Leaf) }; throw }
    if (Test-Path $prev) { Remove-Item $prev -Recurse -Force -ErrorAction SilentlyContinue }

    # 4. Lối tắt: menu Start và màn hình nền.
    if (-not $NoShortcuts) {
        $shell = New-Object -ComObject WScript.Shell
        $menu = Join-Path ([Environment]::GetFolderPath("Programs")) "HAK Accounting"
        New-Item -ItemType Directory -Force $menu | Out-Null
        # WScript cannot save a shortcut whose name has Vietnamese letters (outside the ANSI code page): save under an ASCII
        # name, then rename.
        function Save-Link($path, [scriptblock]$fill) {
            $tmp = Join-Path (Split-Path $path -Parent) ("hak-" + [guid]::NewGuid().ToString("N") + ".lnk")
            $l = $shell.CreateShortcut($tmp)
            & $fill $l
            $l.Save()
            Move-Item -LiteralPath $tmp -Destination $path -Force
        }
        function Link($path, $target, $desc, $minimized) {
            Save-Link $path { param($l) $l.TargetPath = $target; $l.WorkingDirectory = $InstallDir; $l.IconLocation = "$exe,0"; $l.Description = $desc; if ($minimized) { $l.WindowStyle = 7 } }
        }
        Link (Join-Path $menu "HAK Accounting.lnk") (Join-Path $InstallDir "Chay HAK.cmd") "Mở HAK Accounting" $true
        Link (Join-Path $menu "HAK - thử nghiệm.lnk") (Join-Path $InstallDir "Chay HAK - thu nghiem.cmd") "HAK với CSDL thử nghiệm riêng" $true
        Link (Join-Path $menu "Khôi phục mật khẩu quản trị.lnk") (Join-Path $InstallDir "Khoi phuc mat khau quan tri.cmd") "Đặt lại mật khẩu quản trị (tắt HAK trước)" $false
        Save-Link (Join-Path $menu "Gỡ cài đặt HAK.lnk") { param($l) $l.TargetPath = "powershell.exe"; $l.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""; $l.IconLocation = "$exe,0" }
        Link (Join-Path ([Environment]::GetFolderPath("Desktop")) "HAK Accounting.lnk") (Join-Path $InstallDir "Chay HAK.cmd") "Mở HAK Accounting" $true
    }

    # 5. Mục "Ứng dụng đã cài" (Settings → Apps) để gỡ như phần mềm thường.
    if (-not $NoRegistry) {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HAKAccounting"
        New-Item -Path $key -Force | Out-Null
        $size = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
        $props = @{ DisplayName = "HAK Accounting"; DisplayVersion = $version; Publisher = "HAK"; InstallLocation = $InstallDir; DisplayIcon = "$exe,0"
            UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""; InstallDate = (Get-Date -Format "yyyyMMdd") }
        foreach ($k in $props.Keys) { New-ItemProperty -Path $key -Name $k -Value $props[$k] -PropertyType String -Force | Out-Null }
        foreach ($k in "NoModify", "NoRepair") { New-ItemProperty -Path $key -Name $k -Value 1 -PropertyType DWord -Force | Out-Null }
        New-ItemProperty -Path $key -Name EstimatedSize -Value $size -PropertyType DWord -Force | Out-Null
    }

    Say ""
    Say ("Đã cài xong HAK Accounting {0}{1}." -f $version, $(if ($old) { " (nâng cấp từ $old)" } else { "" }))
    Say "Mở HAK bằng biểu tượng 'HAK Accounting' trên màn hình nền hoặc menu Start."
    if (-not $Quiet) {
        $a = Read-Host "Gõ M rồi Enter để mở HAK ngay, Enter để đóng"
        if ($a -match '^[mM]$') { Start-Process (Join-Path $InstallDir "Chay HAK.cmd") -WorkingDirectory $InstallDir }
    }
    exit 0
}
catch { Fail $_.Exception.Message }
