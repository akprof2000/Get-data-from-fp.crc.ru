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

# Погодовой сбор: список лет периода + отметки о завершённых (works/.years-done)
# и текущем (works/.year-current). При однолетнем периоде таблица не выводится.
function Get-YearPlan {
    try {
        $cfgText = (Get-Content (Join-Path $root "appsettings.json") -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', ''
        $cfg = $cfgText | ConvertFrom-Json
        $from = if ($env:GetSiteData__Search__PeriodStart) { $env:GetSiteData__Search__PeriodStart } else { $cfg.GetSiteData.Search.PeriodStart }
        $to   = if ($env:GetSiteData__Search__PeriodEnd)   { $env:GetSiteData__Search__PeriodEnd }   else { $cfg.GetSiteData.Search.PeriodEnd }
        $y1 = [int]$from.Split('.')[1]; $y2 = [int]$to.Split('.')[1]
        if ($y1 -eq $y2) { return $null }
        $doneYears = @()
        if (Test-Path (Join-Path $works ".years-done")) {
            $doneYears = @(Get-Content (Join-Path $works ".years-done") | Where-Object { $_ -match '^\d{4}$' })
        }
        $cur = ""
        if (Test-Path (Join-Path $works ".year-current")) {
            $cur = (Get-Content (Join-Path $works ".year-current") | Select-Object -First 1)
        }
        return [pscustomobject]@{ Years = @($y1..$y2); Done = $doneYears; Current = $cur; From = $from; To = $to }
    } catch { return $null }
}

$last = "collect"
$lastReal = "collect"
while ($true) {
    # Флаг исчез (конвейер закончил или упал) — это «done»/«failed», а не повод
    # оставить страницу на промежуточном кадре.
    $stage = if (Test-Path $flag) { (Get-Content $flag -ErrorAction SilentlyContinue | Select-Object -First 1) } else { "done" }
    if ($stage) { $last = $stage }
    # Запоминаем последний РЕАЛЬНЫЙ этап: «failed» приходит вместо него, и без
    # этого красным помечался бы первый этап, а не тот, на котором упало.
    if ($order -contains $last) { $lastReal = $last }
    $stage = $last
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
    $idx = if ($stage -eq "failed" -and $lastReal) { $order.IndexOf($lastReal) } else { $order.IndexOf($stage) }
    if ($idx -lt 0) { $idx = 0 }
    $body = ""
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $r = $rows[$i]
        if ($stage -eq "done")   { $st = "ok"; $stTxt = "готово"; $w = 100 }
        elseif ($i -lt $idx)     { $st = "ok"; $stTxt = "готово"; $w = 100 }
        elseif ($i -eq $idx -and $stage -eq "failed") { $st = "err"; $stTxt = "ошибка"; $w = 100 }
        elseif ($i -eq $idx)     { $st = "run"; $stTxt = "идёт"; $w = 55 }
        else                     { $st = "wait"; $stTxt = "ожидает"; $w = 0 }
        $body += "<tr><td>$($r[0])</td><td class='$st'>$stTxt</td>" +
                 "<td><div class='bar'><i class='$st' style='width:$w%'></i></div></td>" +
                 "<td class='num'>$($r[2])</td></tr>`n"
    }
    # Подстатус по годам (только для многолетних периодов).
    $plan = Get-YearPlan
    $yearsBlock = ""
    if ($plan) {
        $txtByYear = @{}
        foreach ($d in (Get-ChildItem (Join-Path $works "documents") -Directory -ErrorAction SilentlyContinue)) {
            $txtByYear[$d.Name] = (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
        }
        $htmlByYear = @{}
        foreach ($term in (Get-ChildItem (Join-Path $works "output") -Directory -ErrorAction SilentlyContinue)) {
            foreach ($y in (Get-ChildItem $term.FullName -Directory -ErrorAction SilentlyContinue)) {
                $htmlByYear[$y.Name] = [int]$htmlByYear[$y.Name] + (Get-ChildItem $y.FullName -Recurse -File -ErrorAction SilentlyContinue).Count
            }
        }
        $rowsY = ""
        foreach ($y in $plan.Years) {
            $ys = "$y"
            if ($stage -eq "done" -or ($plan.Done -contains $ys)) { $st = "ok"; $stTxt = "собран, HTML удалён"; $w = 100 }
            elseif ($plan.Current -eq $ys)     { $st = "run";  $stTxt = "в работе";            $w = 55 }
            else                               { $st = "wait"; $stTxt = "ожидает";             $w = 0 }
            $htmlNow = [int]$htmlByYear[$ys]
            $txtNow  = [int]$txtByYear[$ys]
            $rowsY += "<tr><td>$ys</td><td class='$st'>$stTxt</td>" +
                      "<td><div class='bar'><i class='$st' style='width:$w%'></i></div></td>" +
                      "<td class='num'>$htmlNow HTML · $txtNow txt</td></tr>`n"
        }
        $left = if ($stage -eq "done") { 0 } else { ($plan.Years | Where-Object { $plan.Done -notcontains "$_" }).Count }
        $yearsBlock = @"
<h2 style="font-size:19px;margin:26px 0 6px">Годы периода ($($plan.From) — $($plan.To))</h2>
<p class="note" style="margin:0 0 10px">Осталось обработать лет: <b>$left</b> из $($plan.Years.Count). HTML каждого года удаляется сразу после разбора в тексты; тексты накапливаются.</p>
<table><tr><th>Год</th><th>Статус</th><th style="width:170px">Прогресс</th><th>Счётчики</th></tr>
$rowsY</table>
"@
    }

    $stamp = Get-Date -Format "HH:mm:ss · dd.MM.yyyy"
    $final = ($stage -eq "done" -or $stage -eq "failed")
    $title = if ($stage -eq "done") { "Конвейер завершён" } elseif ($stage -eq "failed") { "Конвейер остановлен ошибкой" } else { "Конвейер работает" }
    # Готовую страницу больше не перезагружаем: refresh нужен только в работе.
    $meta  = if ($final) { "" } else { '<meta http-equiv="refresh" content="2">' }
    $sub   = if ($stage -eq "done") { "работа завершена" } elseif ($stage -eq "failed") { "подробности — в logs/" } else { "страница сама обновляется каждые 2 с" }
@"
<!doctype html><html lang="ru"><head><meta charset="utf-8">
$meta
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
.run{color:#b8860b;font-weight:700}.wait{color:#8a94a0;font-weight:700}.ok{color:#1a8a4a;font-weight:700}.err{color:#b3261e;font-weight:700}
.bar{height:8px;background:#e7ebe9;border-radius:4px;overflow:hidden;min-width:120px}
.bar i{display:block;height:100%;background:#0a6e5c}
.bar i.run{background:#b8860b;animation:p 1.2s ease-in-out infinite alternate}
.bar i.err{background:#b3261e}
@keyframes p{from{opacity:.45}to{opacity:1}}
.note{color:#6b7a86;font-size:14px;margin-top:14px}
</style></head><body><div class="wrap">
<h1>$title</h1>
<span class="stamp">обновлено $stamp — $sub</span>
<table><tr><th>Этап</th><th>Статус</th><th style="width:170px">Прогресс</th><th>Счётчики</th></tr>
$body</table>
$yearsBlock
<p class="note">Этапы инкрементальные: повторный запуск докачивает и дообрабатывает только новое. Подробности — в logs/&lt;приложение&gt;.log.</p>
</div></body></html>
"@ | Set-Content -Path $page -Encoding UTF8
    if ($last -eq "done" -or $last -eq "failed") { break }
    Start-Sleep -Seconds 2
}
