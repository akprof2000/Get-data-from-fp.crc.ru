# Фоновый писатель живой страницы статуса конвейера (запускается из run-pipeline.cmd).
# Каждые 5 секунд считает файлы по этапам и переписывает works/pipeline-status.html;
# страница обновляется сама (<meta refresh>). Выходит, когда исчезает флаг-файл.
$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$works = Join-Path $root "works"
$flag  = Join-Path $works ".pipeline-running"
$page  = Join-Path $works "pipeline-status.html"
New-Item -ItemType Directory -Force $works | Out-Null

function CountFiles($p) { (Get-ChildItem (Join-Path $works $p) -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count }

while (Test-Path $flag) {
    $stage = (Get-Content $flag -ErrorAction SilentlyContinue | Select-Object -First 1)
    $c = @{
        html  = CountFiles "output"
        txt   = CountFiles "documents"
        cells = CountFiles "cells"
        json  = CountFiles "OutputJson"
        norm  = CountFiles "OutputNormalized"
    }
    $rows = @(
        @("1. Сбор с fp.crc.ru",          "collect",   "$($c.html) HTML"),
        @("2. HTML → тексты",             "parse",     "$($c.txt) txt"),
        @("3. ML-классификация",          "ml",        "$($c.cells) про БС"),
        @("4. Извлечение в JSON",         "extract",   "$($c.json) JSON"),
        @("5. Обновление базы ГАР+OSM",   "garupdate", "gar.sqlite"),
        @("6. Нормализация адресов",      "normalize", "$($c.norm) готово")
    )
    $order = @("collect","parse","ml","extract","garupdate","normalize")
    $idx = [Math]::Max(0, $order.IndexOf($stage))
    $body = ""
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $r = $rows[$i]
        if ($stage -eq "done") { $st = "ok"; $stTxt = "готово"; $w = 100 }
        elseif ($i -lt $idx)   { $st = "ok"; $stTxt = "готово"; $w = 100 }
        elseif ($i -eq $idx)   { $st = "run"; $stTxt = "идёт"; $w = 55 }
        else                   { $st = "wait"; $stTxt = "ожидает"; $w = 0 }
        $body += "<tr><td>$($r[0])</td><td class='$st'>$stTxt</td>" +
                 "<td><div class='bar'><i class='$st' style='width:$w%'></i></div></td>" +
                 "<td class='num'>$($r[2])</td></tr>`n"
    }
    $stamp = Get-Date -Format "HH:mm:ss · dd.MM.yyyy"
    $title = if ($stage -eq "done") { "Конвейер завершён" } else { "Конвейер работает" }
@"
<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta http-equiv="refresh" content="5">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 viewBox=%270 0 100 100%27%3E%3Ctext y=%27.9em%27 font-size=%2790%27%3E%F0%9F%93%A1%3C/text%3E%3C/svg%3E"><title>Пульт конвейера fp.crc.ru</title>
<style>
body{margin:0;padding:32px 16px;background:#f6f5f1;color:#22303a;font:16px/1.5 system-ui,sans-serif}
.wrap{max-width:840px;margin:0 auto}
h1{font-size:26px;margin:0 0 6px}
.stamp{display:inline-block;background:#fff;border:1px solid #dfe3e0;border-radius:6px;padding:2px 10px;font-family:monospace;font-size:14px;margin-bottom:14px}
table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #dfe3e0}
th,td{padding:10px 14px;text-align:left;border-bottom:1px solid #dfe3e0}
th{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#6b7a86}
td.num{font-family:monospace;white-space:nowrap}
.run{color:#b8860b;font-weight:700}.wait{color:#8a94a0;font-weight:700}.ok{color:#1a8a4a;font-weight:700}
.bar{height:8px;background:#e7ebe9;border-radius:4px;overflow:hidden;min-width:120px}
.bar i{display:block;height:100%;background:#0a6e5c}
.bar i.run{background:#b8860b;animation:p 1.2s ease-in-out infinite alternate}
@keyframes p{from{opacity:.45}to{opacity:1}}
.note{color:#6b7a86;font-size:14px;margin-top:14px}
</style></head><body><div class="wrap">
<h1>$title</h1>
<span class="stamp">обновлено $stamp — страница сама обновляется каждые 5 с</span>
<table><tr><th>Этап</th><th>Статус</th><th style="width:170px">Прогресс</th><th>Счётчики</th></tr>
$body</table>
<p class="note">Этапы инкрементальные: повторный запуск докачивает и дообрабатывает только новое. Подробности — в logs/&lt;приложение&gt;.log.</p>
</div></body></html>
"@ | Set-Content -Path $page -Encoding UTF8
    Start-Sleep -Seconds 5
}
# финальный кадр
if (Test-Path $page) { (Get-Content $page -Raw) -replace "Конвейер работает","Конвейер завершён" | Set-Content $page -Encoding UTF8 }
