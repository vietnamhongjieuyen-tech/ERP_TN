# Lưu toàn văn một tệp Word Công báo thành .txt UTF-8 (mở CHỈ ĐỌC bằng Microsoft Word qua COM). Dùng cho verify-formulas.py.
# Dùng: powershell -File word-text.ps1 <tệp .doc> <tệp .txt ra>
param([Parameter(Mandatory)][string]$Doc, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = "Stop"
$word = New-Object -ComObject Word.Application
$word.Visible = $false
try {
    $d = $word.Documents.Open((Resolve-Path $Doc).Path, $false, $true)
    $text = $d.Content.Text -replace "`r", "`n" -replace "[\a\v]", " "
    [IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))
    $d.Close($false)
    "$Doc : $($text.Length) ký tự"
}
finally { $word.Quit() }
