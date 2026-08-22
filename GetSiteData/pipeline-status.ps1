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
    # Выход нормализатора может быть архивом (works/OutputNormalized.zip) — тогда
    # считаем записи в нём и в его томах: оглавление читается мгновенно, без распаковки.
    if ($p -like "*.zip") {
        $n = 0
        $dir = Split-Path -Parent $full
        $base = [IO.Path]::GetFileNameWithoutExtension($full)
        if (Test-Path $dir) {
            foreach ($vol in @(Get-ChildItem -LiteralPath $dir -Filter "$base*.zip" -File -EA SilentlyContinue)) {
                try {
                    $zip = [IO.Compression.ZipFile]::OpenRead($vol.FullName)
                    $n += $zip.Entries.Count
                    $zip.Dispose()
                } catch { }
            }
        }
        return $n
    }
    if (-not (Test-Path $full)) { return 0 }
    try {
        $n = 0
        foreach ($f in [System.IO.Directory]::EnumerateFiles($full, '*', [System.IO.SearchOption]::AllDirectories)) { $n++ }
        return $n
    } catch { return 0 }
}

# Счётчики считает ОТДЕЛЬНЫЙ ПОТОК, а кадр рисуется каждые 2 секунды из готовых
# цифр. Раньше пересчёт шёл в том же потоке: обход каталога на 700 тыс. файлов
# занимает больше минуты, всё это время страница не обновлялась и выглядела
# зависшей. Теперь она живая всегда, а цифры подтягиваются по мере готовности.
Add-Type -AssemblyName System.IO.Compression.FileSystem -EA SilentlyContinue

# Путь выхода нормализатора берём из конфига: там может стоять архив.
$normPath = "OutputNormalized"
try {
    $cfgFile = Join-Path $root "appsettings.json"
    if (Test-Path $cfgFile) {
        $cfgText = (Get-Content $cfgFile -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', ''
        $cfg = $cfgText | ConvertFrom-Json
        if ($cfg.NormalizeAddress.OutputNormalizedPath) { $normPath = $cfg.NormalizeAddress.OutputNormalizedPath }
    }
} catch { }

$script:counts = [hashtable]::Synchronized(@{ html = 0; txt = 0; cells = 0; other = 0; json = 0; norm = 0; ready = $false })
$countDirs = @(
    @{ Key = "json";  Dir = "OutputJson" },
    @{ Key = "cells"; Dir = "cells" },
    @{ Key = "other"; Dir = "other" },
    @{ Key = "txt";   Dir = "documents" },
    @{ Key = "html";  Dir = "output" },
    @{ Key = "norm";  Dir = $normPath }
)

$counterRunspace = [runspacefactory]::CreateRunspace()
$counterRunspace.Open()
$counterRunspace.SessionStateProxy.SetVariable('counts', $script:counts)
$counterRunspace.SessionStateProxy.SetVariable('works', $works)
$counterRunspace.SessionStateProxy.SetVariable('countDirs', $countDirs)
$counterRunspace.SessionStateProxy.SetVariable('flag', $flag)
$counterPs = [powershell]::Create()
$counterPs.Runspace = $counterRunspace
[void]$counterPs.AddScript({
    Add-Type -AssemblyName System.IO.Compression.FileSystem -EA SilentlyContinue
    function CountOne($works, $p) {
        $full = Join-Path $works $p
        if ($p -like "*.zip") {
            $n = 0
            $dir = Split-Path -Parent $full
            $base = [IO.Path]::GetFileNameWithoutExtension($full)
            if (Test-Path $dir) {
                foreach ($vol in @(Get-ChildItem -LiteralPath $dir -Filter "$base*.zip" -File -EA SilentlyContinue)) {
                    try { $zip = [IO.Compression.ZipFile]::OpenRead($vol.FullName); $n += $zip.Entries.Count; $zip.Dispose() } catch { }
                }
            }
            return $n
        }
        if (-not (Test-Path $full)) { return 0 }
        try {
            $n = 0
            foreach ($f in [System.IO.Directory]::EnumerateFiles($full, '*', [System.IO.SearchOption]::AllDirectories)) { $n++ }
            return $n
        } catch { return 0 }
    }
    $lastYears = [datetime]::MinValue
    while ($true) {
        foreach ($e in $countDirs) {
            $counts[$e.Key] = CountOne $works $e.Dir
            $counts["ready"] = $true
        }
        # Разбивка по годам — тоже тяжёлый обход (сотни тысяч текстов), и в главном
        # потоке он замораживал страницу на минуты. Считаем здесь, раз в минуту.
        if (((Get-Date) - $lastYears).TotalSeconds -ge 60) {
            $txt = @{}; $html = @{}
            # documents/<год>/<месяц>/*.txt
            $docs = Join-Path $works "documents"
            if (Test-Path $docs) {
                foreach ($dir in [IO.Directory]::EnumerateDirectories($docs)) {
                    $n = 0
                    try { foreach ($f in [IO.Directory]::EnumerateFiles($dir, '*', [IO.SearchOption]::AllDirectories)) { $n++ } } catch { }
                    $txt[[IO.Path]::GetFileName($dir)] = $n
                }
            }
            # output/<термин>/<год>/… — год на ВТОРОМ уровне, счёт суммируется по терминам
            $outDir = Join-Path $works "output"
            if (Test-Path $outDir) {
                foreach ($term in [IO.Directory]::EnumerateDirectories($outDir)) {
                    foreach ($yd in [IO.Directory]::EnumerateDirectories($term)) {
                        $n = 0
                        try { foreach ($f in [IO.Directory]::EnumerateFiles($yd, '*', [IO.SearchOption]::AllDirectories)) { $n++ } } catch { }
                        $name = [IO.Path]::GetFileName($yd)
                        $html[$name] = [int]$html[$name] + $n
                    }
                }
            }
            $counts["yearsTxt"] = $txt
            $counts["yearsHtml"] = $html
            $lastYears = Get-Date
        }
        Start-Sleep -Milliseconds 500
        # Конвейер закончился — считать больше нечего.
        if (-not (Test-Path $flag)) { break }
    }
})
$counterHandle = $counterPs.BeginInvoke()

function Get-Counts { return $script:counts }

$script:yearCache = $null
$script:yearStamp = [datetime]::MinValue
function Get-YearCounts {
    # Считает фоновый поток (см. выше): обход сотен тысяч файлов в главном потоке
    # замораживал страницу на минуты, и пульт выглядел зависшим.
    return @{
        Txt  = if ($script:counts["yearsTxt"])  { $script:counts["yearsTxt"] }  else { @{} }
        Html = if ($script:counts["yearsHtml"]) { $script:counts["yearsHtml"] } else { @{} }
    }
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

# Момент начала прогона храним в файле: писатель статуса могли перезапустить,
# а «прошло с начала» должно считаться от старта КОНВЕЙЕРА, а не от старта окна.
# Файл заводится при первом кадре и снимается вместе с флагом .pipeline-running.
$startFile = Join-Path $works ".pipeline-started"
if (Test-Path $flag) {
    if (-not (Test-Path $startFile)) {
        [System.IO.File]::WriteAllText($startFile, (Get-Date).ToString("o"), (New-Object System.Text.UTF8Encoding($false)))
    }
} elseif (Test-Path $startFile) {
    Remove-Item $startFile -Force -ErrorAction SilentlyContinue
}
# Разбор строго инвариантный: на русской локали [datetime]::Parse не принимает
# формат "o", отметка терялась и «прошло с начала» сбрасывалось в ноль.
$script:startedAt = Get-Date
try {
    $raw = (Get-Content $startFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($raw) {
        $script:startedAt = [datetime]::Parse($raw, [System.Globalization.CultureInfo]::InvariantCulture,
                                              [System.Globalization.DateTimeStyles]::RoundtripKind)
    }
} catch { }

# История «сделано» по текущему этапу для оценки остатка: скорость берём по
# скользящему окну (последние ~5 минут), иначе оценка скачет на паузах.
$script:rateHist = @()

function Fmt-Span([double]$seconds) {
    if ($seconds -lt 0) { return "—" }
    $ts = [TimeSpan]::FromSeconds([Math]::Round($seconds))
    if ($ts.TotalDays -ge 1) { return ("{0}д {1:00}ч {2:00}м" -f [int]$ts.TotalDays, $ts.Hours, $ts.Minutes) }
    if ($ts.TotalHours -ge 1) { return ("{0}ч {1:00}м" -f [int]$ts.TotalHours, $ts.Minutes) }
    if ($ts.TotalMinutes -ge 1) { return ("{0}м {1:00}с" -f [int]$ts.TotalMinutes, $ts.Seconds) }
    return ("{0}с" -f [int]$ts.TotalSeconds)
}

# Оценка остатка по фактической скорости текущего этапа. Возвращает готовую
# строку: пока точек мало или прогресс стоит — честное «оценка накапливается»,
# а не выдуманное число.
function Get-Eta($doneN, $totalN, $stageName) {
    if ($totalN -le 0 -or $doneN -ge $totalN) { return "—" }
    $now = Get-Date
    $script:rateHist += ,([pscustomobject]@{ T = $now; Done = [double]$doneN; Stage = $stageName })
    # Смена этапа обнуляет историю: счётчики разных этапов несопоставимы.
    $script:rateHist = @($script:rateHist | Where-Object { $_.Stage -eq $stageName -and ($now - $_.T).TotalSeconds -le 300 })
    if ($script:rateHist.Count -lt 2) { return "оценка накапливается" }
    $first = $script:rateHist[0]
    $span = ($now - $first.T).TotalSeconds
    $delta = [double]$doneN - $first.Done
    if ($span -lt 20 -or $delta -le 0) { return "оценка накапливается" }
    $rate = $delta / $span
    return (Fmt-Span (($totalN - $doneN) / $rate))
}

$order = @("collect","parse","ml","extract","garupdate","normalize")
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
    # В zip-режиме содержимое ОТКРЫТОГО тома снаружи не видно (оглавление
    # пишется при закрытии), поэтому проценты берём из works/.normalize-progress,
    # который ведёт сам нормализатор; файла нет — считаем по выходу, как раньше.
    $normDone = $c.norm
    $normTotal = $c.json
    $progFile = Join-Path $works ".normalize-progress"
    if (Test-Path $progFile) {
        try {
            $parts = ((Get-Content $progFile -Raw -EA SilentlyContinue) -replace '\s', '') -split '/'
            if ($parts.Count -eq 2) {
                $pd = [int]$parts[0]; $pt = [int]$parts[1]
                if ($pt -gt 0) { $normDone = $pd; $normTotal = $pt }
            }
        } catch { }
    }
    $fNormalize = Fmt $normDone $normTotal "готово"
    $rows = @(
        @("1. Сбор с fp.crc.ru",          "collect",   $fCollect.Text,   $fCollect.Pct),
        @("2. HTML → тексты",             "parse",     $fParse.Text,     $fParse.Pct),
        @("3. ML-классификация",          "ml",        $fMl.Text,        $fMl.Pct),
        @("4. Извлечение в JSON",         "extract",   $fExtract.Text,   $fExtract.Pct),
        @("5. Обновление базы ГАР+OSM",   "garupdate", "gar.sqlite",     -1),
        @("6. Нормализация адресов",      "normalize", $fNormalize.Text, $fNormalize.Pct)
    )
    # Два счётчика времени: сколько идёт прогон и сколько осталось по скорости
    # ТЕКУЩЕГО этапа (остальные этапы предсказать нельзя — объём заранее неизвестен).
    $elapsedTxt = Fmt-Span ((Get-Date) - $script:startedAt).TotalSeconds
    $etaPair = switch ($stage) {
        "parse"     { @($c.txt,   ($c.txt + $c.html)) }
        "ml"        { @($mlDone,  $c.txt) }
        "extract"   { @($c.json,  $c.cells) }
        "normalize" { @($normDone, $normTotal) }
        default     { @(0, 0) }
    }
    $etaTxt = if ($stage -eq "done") { "завершено" }
              elseif ($stage -eq "failed") { "—" }
              else { Get-Eta $etaPair[0] $etaPair[1] $stage }

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
<details class="folded" id="years">
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
    $autoReload = if ($final) { "false" } else { "true" }
    $sub   = if ($stage -eq "done") { "работа завершена" } elseif ($stage -eq "failed") { "подробности — в logs/" } else { "страница сама обновляется каждые 2 с (состояние блоков сохраняется)" }
@"
<!doctype html><html lang="ru"><head><meta charset="utf-8">
<noscript><meta http-equiv="refresh" content="5"></noscript>
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
.times{display:flex;gap:12px;margin:14px 0 4px;flex-wrap:wrap}
.tcard{flex:1 1 200px;background:#fff;border:1px solid #dfe3e0;border-radius:6px;padding:10px 14px}
.tlabel{display:block;color:#6b7a86;font-size:13px}
.tval{display:block;font-size:22px;font-weight:700;color:#1a3d2f;margin-top:2px;font-variant-numeric:tabular-nums}
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
<div class="times">
  <div class="tcard"><span class="tlabel">Прошло с начала</span><span class="tval">$elapsedTxt</span></div>
  <div class="tcard"><span class="tlabel">Осталось (оценка)</span><span class="tval">$etaTxt</span></div>
</div>
<table><tr><th>Этап</th><th>Статус</th><th style="width:170px">Прогресс</th><th>Счётчики</th></tr>
$body</table>
$yearsBlock
<p class="note">Этапы инкрементальные: повторный запуск докачивает и дообрабатывает только новое. Подробности — в logs/&lt;приложение&gt;.log.</p>
</div>
<script>
// Страница сама перезагружается каждые 2 секунды, поэтому раскрытый блок
// схлопывался. Состояние держим в адресной строке (#years-open) — оно
// переживает перезагрузку даже при открытии файла с диска; заодно возвращаем
// позицию прокрутки.
(function () {
  var AUTO = $autoReload;          // пока конвейер работает — обновляемся сами
  var box = document.getElementById("years");

  // Состояние раскрытого блока держим в хеше адреса: он переживает и нашу
  // перезагрузку, и ручное обновление страницы (F5). history.replaceState на
  // file:// браузеры часто запрещают, поэтому пишем location.hash напрямую.
  function remember(open) {
    var want = open ? "#years-open" : "#years-closed";
    if (location.hash !== want) {
      try { history.replaceState(null, "", want); } catch (e) { location.hash = want; }
    }
  }
  if (box) {
    if (location.hash === "#years-open") { box.open = true; }
    box.addEventListener("toggle", function () { remember(box.open); });
  }

  // Позиция прокрутки — тоже через хеш-независимое хранилище с запасным вариантом.
  var KEY = "pultScroll";
  function saveScroll() {
    try { sessionStorage.setItem(KEY, String(window.scrollY)); } catch (e) { window.name = "s" + window.scrollY; }
  }
  function readScroll() {
    try {
      var v = sessionStorage.getItem(KEY);
      if (v !== null) { return parseInt(v, 10); }
    } catch (e) {}
    if (window.name && window.name.charAt(0) === "s") { return parseInt(window.name.slice(1), 10) || 0; }
    return 0;
  }
  var y = readScroll();
  if (y > 0) { window.scrollTo(0, y); }
  window.addEventListener("beforeunload", saveScroll);

  // location.reload() сохраняет полный адрес вместе с хешем — в отличие от
  // <meta http-equiv="refresh">, после которого блок схлопывался.
  if (AUTO) {
    setTimeout(function () { saveScroll(); location.reload(); }, 2000);
  }
})();
</script>
</body></html>
"@ | Set-Content -Path "$page.tmp" -Encoding UTF8
    # Пишем во временный файл и переименовываем: страница перезаписывается каждые
    # 2 секунды, и браузер успевал прочитать её в момент записи — получал обрезанный
    # HTML без скрипта, после чего автообновление прекращалось до ручного F5.
    Move-Item -Path "$page.tmp" -Destination $page -Force
    if ($last -eq "done" -or $last -eq "failed") { break }
    Start-Sleep -Seconds 2
}
