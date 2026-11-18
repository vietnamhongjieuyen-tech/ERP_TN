# Xuất mọi bảng của một tệp Word Công báo thành <tiền tố>_tNNN.tsv (hàng<TAB>cột<TAB>chữ, đánh số từ 1).
# Dùng Microsoft Word (COM), mở CHỈ ĐỌC. Ô gộp nhiều dòng giữ chữ nối bằng " | ". Đầu vào cho templates.py, b09.py, verify.py.
# Dùng: powershell -File word-tables.ps1 <tệp .doc> <thư mục ra> <tiền tố, vd 1577>
param([Parameter(Mandatory)][string]$Doc, [Parameter(Mandatory)][string]$Out, [Parameter(Mandatory)][string]$Prefix)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
$word = New-Object -ComObject Word.Application
$word.Visible = $false
try {
    $d = $word.Documents.Open((Resolve-Path $Doc).Path, $false, $true)   # ConfirmConversions=false, ReadOnly=true
    $n = $d.Tables.Count
    for ($i = 1; $i -le $n; $i++) {
        $sb = New-Object System.Text.StringBuilder
        foreach ($cell in $d.Tables.Item($i).Range.Cells) {
            $text = $cell.Range.Text -replace "[\r\a]+$", "" -replace "[\r\n\v]+", " | " -replace "\s+", " "
            [void]$sb.AppendLine("$($cell.RowIndex)`t$($cell.ColumnIndex)`t$($text.Trim())")
        }
        [IO.File]::WriteAllText((Join-Path $Out ("{0}_t{1:D3}.tsv" -f $Prefix, $i)), $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
    }
    $d.Close($false)
    "$Doc : $n bảng"
}
finally { $word.Quit() }
