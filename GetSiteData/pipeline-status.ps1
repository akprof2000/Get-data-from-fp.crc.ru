# Фоновый писатель живой страницы статуса конвейера (запускается из run-pipeline.cmd).
# Каждые 5 секунд считает файлы по этапам и переписывает works/pipeline-status.html;
# страница обновляется сама (<meta refresh>). Выходит, когда исчезает флаг-файл.
$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$works = Join-Path $root "works"
$flag  = Join-Path $works ".pipeline-running"
$page  = Join-Path $works "pipeline-status.html"
New-Item -ItemType Directory -Force $works | Out-Null

# Счёт файлов потоковым обходом: Get-ChildItem -Recurse строит объект на каждый
# файл и на сотнях тысяч текстов считал минутами — страница переставала
# обновляться и выглядела зависшей.
function CountFiles($p) {
    $full = Join-Path $works $p
    if (-not (Test-Path $full)) { return 0 }
    try {
        $n = 0
        foreach ($f in [System.IO.Directory]::EnumerateFiles($full, '*', [System.IO.SearchOption]::AllDirectories)) { $n++ }
        return $n
    } catch { return 0 }
}

# Тяжёлые счётчики пересчитываем не чаще раза в 15 секунд; сам кадр (время,
# этап, годы) рисуется каждые 2 секунды и страница остаётся «живой».
$script:countCache = $null
$script:countStamp = [datetime]::MinValue
function Get-Counts {
    if ($script:countCache -and ((Get-Date) - $script:countStamp).TotalSeconds -lt 15) { return $script:countCache }
    $script:countCache = @{
        html  = CountFiles "output";     txt   = CountFiles "documents"
        cells = CountFiles "cells";      other = CountFiles "other"
        json  = CountFiles "OutputJson"; norm  = CountFiles "OutputNormalized"
    }
    $script:countStamp = Get-Date
    return $script:countCache
}

$script:yearCache = $null
$script:yearStamp = [datetime]::MinValue
function Get-YearCounts {
    if ($script:yearCache -and ((Get-Date) - $script:yearStamp).TotalSeconds -lt 15) { return $script:yearCache }
    $txt = @{}; $html = @{}
    $docs = Join-Path $works "documents"
    if (Test-Path $docs) {
        foreach ($dir in [System.IO.Directory]::EnumerateDirectories($docs)) {
            $n = 0
            foreach ($f in [System.IO.Directory]::EnumerateFiles($dir, '*', [System.IO.SearchOption]::AllDirectories)) { $n++ }
            $txt[[System.IO.Path]::GetFileName($dir)] = $n
        }
    }
    $out = Join-Path $works "output"
    if (Test-Path $out) {
        foreach ($term in [System.IO.Directory]::EnumerateDirectories($out)) {
            foreach ($yd in [System.IO.Directory]::EnumerateDirectories($term)) {
                $n = 0
                foreach ($f in [System.IO.Directory]::EnumerateFiles($yd, '*', [System.IO.SearchOption]::AllDirectories)) { $n++ }
                $name = [System.IO.Path]::GetFileName($yd)
                $html[$name] = [int]$html[$name] + $n
            }
        }
    }
    $script:yearCache = @{ Txt = $txt; Html = $html }
    $script:yearStamp = Get-Date
    return $script:yearCache
}

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
        # BOM у файлов отметок отсекаем: иначе первый год выглядел бы несобранным.
        $doneYears = @()
        if (Test-Path (Join-Path $works ".years-done")) {
            $doneYears = @(Get-Content (Join-Path $works ".years-done") -Encoding UTF8 |
                           ForEach-Object { $_.Trim([char]0xFEFF, ' ') } | Where-Object { $_ -match '^\d{4}$' })
        }
        $cur = ""
        if (Test-Path (Join-Path $works ".year-current")) {
            $cur = ((Get-Content (Join-Path $works ".year-current") -Encoding UTF8 | Select-Object -First 1) -replace [char]0xFEFF, '').Trim()
        }
        return [pscustomobject]@{ Years = @($y1..$y2); Done = $doneYears; Current = $cur; From = $from; To = $to }
    } catch { return $null }
}

$last = "collect"
$lastReal = "collect"
while ($true) {
    # Флаг исчез (конвейер закончил или упал) — это «done»/«failed», а не повод
    # оставить страницу на промежуточном кадре.
    $stage = if (Test-Path $flag) { ((Get-Content $flag -ErrorAction SilentlyContinue | Select-Object -First 1) -replace [char]0xFEFF, '').Trim() } else { "done" }
    if ($stage) { $last = $stage }
    # Запоминаем последний РЕАЛЬНЫЙ этап: «failed» приходит вместо него, и без
    # этого красным помечался бы первый этап, а не тот, на котором упало.
    if ($order -contains $last) { $lastReal = $last }
    $stage = $last
    $c = Get-Counts
    # Прогресс внутри длинных этапов: выход предыдущего этапа — это «всего»,
    # выход текущего — «сделано». Иначе долгие этапы (ML на сотнях тысяч текстов)
    # часами показывали безликое «идёт».
    function Fmt($doneN, $totalN, $unit) {
        if ($totalN -gt 0) {
            $pct = [Math]::Min(100, [Math]::Floor(100 * $doneN / $totalN))
            return @{ Text = ("{0:N0} / {1:N0} {2} ({3}%)" -f $doneN, $totalN, $unit, $pct); Pct = $pct }
        }
        return @{ Text = ("{0:N0} {1}" -f $doneN, $unit); Pct = -1 }
    }
    $mlDone = $c.cells + $c.other
    $fCollect   = Fmt $c.html 0 "HTML"
    $fParse     = Fmt $c.txt ($c.txt + $c.html) "txt"
    $fMl        = Fmt $mlDone $c.txt "текстов"
    $fExtract   = Fmt $c.json $c.cells "JSON"
    $fNormalize = Fmt $c.norm $c.json "готово"
    $rows = @(
        @("1. Сбор с fp.crc.ru",          "collect",   $fCollect.Text,   $fCollect.Pct),
        @("2. HTML → тексты",             "parse",     $fParse.Text,     $fParse.Pct),
        @("3. ML-классификация",          "ml",        $fMl.Text,        $fMl.Pct),
        @("4. Извлечение в JSON",         "extract",   $fExtract.Text,   $fExtract.Pct),
        @("5. Обновление базы ГАР+OSM",   "garupdate", "gar.sqlite",     -1),
        @("6. Нормализация адресов",      "normalize", $fNormalize.Text, $fNormalize.Pct)
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
        elseif ($i -eq $idx)     {
            $st = "run"
            $w = if ($r[3] -ge 0) { [int]$r[3] } else { 55 }
            $stTxt = if ($r[3] -ge 0) { "идёт · $($r[3])%" } else { "идёт" }
        }
        else                     { $st = "wait"; $stTxt = "ожидает"; $w = 0 }
        $body += "<tr><td>$($r[0])</td><td class='$st'>$stTxt</td>" +
                 "<td><div class='bar'><i class='$st' style='width:$w%'></i></div></td>" +
                 "<td class='num'>$($r[2])</td></tr>`n"
    }
    # Подстатус по годам (только для многолетних периодов).
    $plan = Get-YearPlan
    $yearsBlock = ""
    if ($plan) {
        $yc = Get-YearCounts
        $txtByYear = $yc.Txt
        $htmlByYear = $yc.Html
        $rowsY = ""
        $rowsDone = ""
        $doneCount = 0
        foreach ($y in $plan.Years) {
            $ys = "$y"
            # Год считаем собранным и по факту: есть тексты и не осталось HTML.
            # Отметки снимаются после прохождения всего периода, и без этого
            # готовые годы показывались бы как «ожидает».
            $doneByFacts = ([int]$txtByYear[$ys] -gt 0 -and [int]$htmlByYear[$ys] -eq 0)
            if ($doneByFacts -or $stage -eq "done" -or ($plan.Done -contains $ys)) { $doneCount++ }
            if ($stage -eq "done" -or ($plan.Done -contains $ys) -or $doneByFacts) { $st = "ok"; $stTxt = "собран, HTML удалён"; $w = 100 }
            elseif ($plan.Current -eq $ys)     { $st = "run";  $stTxt = "в работе";            $w = 55 }
            else                               { $st = "wait"; $stTxt = "ожидает";             $w = 0 }
            $htmlNow = [int]$htmlByYear[$ys]
            $txtNow  = [int]$txtByYear[$ys]
            $rowHtml = "<tr><td>$ys</td><td class='$st'>$stTxt</td>" +
                       "<td><div class='bar'><i class='$st' style='width:$w%'></i></div></td>" +
                       "<td class='num'>$htmlNow HTML · $txtNow txt</td></tr>`n"
            # Завершённые годы прячем в сворачиваемый блок: при периоде в 17 лет
            # готовые строки вытесняли со экрана то, что реально в работе.
            if ($st -eq "ok") { $rowsDone += $rowHtml } else { $rowsY += $rowHtml }
        }
        $left = if ($stage -eq "done") { 0 } else {
            ($plan.Years | Where-Object {
                $ys2 = "$_"
                ($plan.Done -notcontains $ys2) -and -not ([int]$txtByYear[$ys2] -gt 0 -and [int]$htmlByYear[$ys2] -eq 0)
            }).Count
        }
        $head = "<tr><th>Год</th><th>Статус</th><th style=`"width:170px`">Прогресс</th><th>Счётчики</th></tr>"
        $activeTable = if ($rowsY) { "<table>$head`n$rowsY</table>" } else { "<p class=`"note`" style=`"margin:0`">Активных годов нет — весь период собран.</p>" }
        $doneBlock = ""
        if ($rowsDone) {
            $doneBlock = @"
<details class="folded">
<summary>Собранные годы: $doneCount — развернуть</summary>
<table>$head
$rowsDone</table>
</details>
"@
        }
        $yearsBlock = @"
<h2>Годы периода ($($plan.From) — $($plan.To))</h2>
<p class="note" style="margin:0 0 10px">Осталось обработать лет: <b>$left</b> из $($plan.Years.Count). HTML каждого года удаляется сразу после разбора в тексты; тексты накапливаются.</p>
$activeTable
$doneBlock
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
h2{font-size:19px;margin:26px 0 6px}
details.folded{margin-top:12px}
details.folded>summary{cursor:pointer;padding:8px 12px;background:#fff;border:1px solid #dfe3e0;border-radius:6px;font-weight:700;color:#1a8a4a;list-style:none}
details.folded>summary::-webkit-details-marker{display:none}
details.folded>summary::before{content:"▸ ";color:#6b7a86}
details.folded[open]>summary::before{content:"▾ "}
details.folded>table{margin-top:8px}
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
