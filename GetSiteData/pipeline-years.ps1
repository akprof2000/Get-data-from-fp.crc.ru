# Погодовой сбор: когда период охватывает больше одного года, качать всё сразу
# нельзя — HTML занимает десятки гигабайт. Скрипт идёт от самого раннего года к
# позднему: собирает год → разбирает его в тексты → удаляет HTML этого года →
# помечает год выполненным. Тексты (works/documents) остаются и накапливаются.
#
# Перезапуск после сбоя/перезагрузки безопасен: годы из works/.years-done
# пропускаются, а год из works/.year-current начинается заново — GetSiteData
# инкрементален и докачает только недостающие документы.
param([string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path))

$ErrorActionPreference = "Stop"
# Приложения конвейера резолвят относительные пути (WorkRoot=works) от ТЕКУЩЕГО
# каталога процесса, поэтому переходим в каталог поставки: иначе works/ окажется
# там, откуда запустили скрипт.
Set-Location $Root
$works   = Join-Path $Root "works"
$done    = Join-Path $works ".years-done"
$current = Join-Path $works ".year-current"
$flag    = Join-Path $works ".pipeline-running"
New-Item -ItemType Directory -Force $works | Out-Null

# Период берём из appsettings.json (JSONC — вырезаем комментарии) с учётом
# переопределения переменными окружения, как это делает сам конвейер.
$cfgText = (Get-Content (Join-Path $Root "appsettings.json") -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', ''
$cfg = $cfgText | ConvertFrom-Json
$from = if ($env:GetSiteData__Search__PeriodStart) { $env:GetSiteData__Search__PeriodStart } else { $cfg.GetSiteData.Search.PeriodStart }
$to   = if ($env:GetSiteData__Search__PeriodEnd)   { $env:GetSiteData__Search__PeriodEnd }   else { $cfg.GetSiteData.Search.PeriodEnd }

# Формат ММ.ГГГГ.
$fromMonth = [int]$from.Split('.')[0]; $fromYear = [int]$from.Split('.')[1]
$toMonth   = [int]$to.Split('.')[0];   $toYear   = [int]$to.Split('.')[1]

# PowerShell по умолчанию пишет UTF8 С BOM, и первая строка файла отметок
# читалась как «﻿2010» — фильтр «^\d{4}$» её не узнавал, поэтому первый год
# при каждом перезапуске собирался заново. Пишем и читаем строго без BOM.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Read-Marks($path) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path -Encoding UTF8 | ForEach-Object { $_.Trim([char]0xFEFF, ' ', [char]9) } | Where-Object { $_ })
}
function Write-Mark($path, $line)  { [System.IO.File]::WriteAllText($path, "$line`r`n", $Utf8NoBom) }
function Append-Mark($path, $line) { [System.IO.File]::AppendAllText($path, "$line`r`n", $Utf8NoBom) }

function Invoke-Stage($exe, $stageName, $arguments) {
    Write-Mark $flag $stageName
    if ($arguments) { & (Join-Path $Root $exe) $arguments } else { & (Join-Path $Root $exe) }
    if ($LASTEXITCODE -ne 0) { throw "$exe завершился с кодом $LASTEXITCODE" }
}

if ($fromYear -eq $toYear) {
    # Один год — обычный порядок, HTML не удаляем (может пригодиться для отладки).
    Invoke-Stage "GetSiteData.exe" "collect" $null
    Invoke-Stage "ParseHTML.exe"   "parse"   $null
    exit 0
}

$doneYears = @(Read-Marks $done | Where-Object { $_ -match '^\d{4}$' })

# Восстановление отметок после переноса данных или потери файла: год, у которого
# УЖЕ есть тексты и не осталось HTML, считаем собранным. Иначе перенесённые
# works/documents ничего не значили бы и все годы качались бы заново.
$backfilled = @()
foreach ($year in $fromYear..$toYear) {
    $ys = "$year"
    if ($doneYears -contains $ys) { continue }
    if ($ys -eq (Read-Marks $current | Select-Object -First 1)) { continue }   # текущий год дособираем
    $txtDir = Join-Path (Join-Path $works "documents") $ys
    if (-not (Test-Path $txtDir)) { continue }
    if ((Get-ChildItem $txtDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) { continue }
    $htmlLeft = 0
    foreach ($term in (Get-ChildItem (Join-Path $works "output") -Directory -ErrorAction SilentlyContinue)) {
        $yd = Join-Path $term.FullName $ys
        if (Test-Path $yd) { $htmlLeft += (Get-ChildItem $yd -Recurse -File -ErrorAction SilentlyContinue).Count }
    }
    if ($htmlLeft -gt 0) { continue }
    Append-Mark $done $ys
    $doneYears += $ys
    $backfilled += $ys
}

Write-Host ""
if ($backfilled.Count -gt 0) {
    Write-Host "Найдены готовые тексты без отметок — отмечаю годы собранными: $($backfilled -join ', ')."
    Write-Host "Если эти годы нужно собрать заново, удалите works/.years-done и соответствующие works/documents/<год>."
}
Write-Host "Период охватывает $fromYear-$toYear — собираем по годам (HTML каждого года удаляется после разбора)."
if ($doneYears.Count -gt 0) { Write-Host "Уже собраны ранее: $($doneYears -join ', ') — пропускаю." }

foreach ($year in $fromYear..$toYear) {
    if ($doneYears -contains "$year") { continue }

    # Внутри года период ограничен только на краях диапазона.
    $mFrom = if ($year -eq $fromYear) { $fromMonth } else { 1 }
    $mTo   = if ($year -eq $toYear)   { $toMonth }   else { 12 }

    Write-Mark $current "$year"
    Write-Host ""
    Write-Host "===== Год $year (месяцы $mFrom-$mTo) ====="

    $env:GetSiteData__Search__PeriodStart = ('{0:00}.{1}' -f $mFrom, $year)
    $env:GetSiteData__Search__PeriodEnd   = ('{0:00}.{1}' -f $mTo,   $year)
    Invoke-Stage "GetSiteData.exe" "collect" $null
    Invoke-Stage "ParseHTML.exe"   "parse"   $null

    # HTML этого года больше не нужен: тексты уже извлечены.
    $outRoot = Join-Path $works "output"
    $freed = 0
    foreach ($dir in (Get-ChildItem $outRoot -Directory -ErrorAction SilentlyContinue)) {
        $yearDir = Join-Path $dir.FullName "$year"
        if (Test-Path $yearDir) {
            $freed += (Get-ChildItem $yearDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
            Remove-Item $yearDir -Recurse -Force
        }
    }
    Write-Host ("Год {0}: HTML удалён, освобождено {1:N0} МБ." -f $year, ($freed / 1MB))

    Append-Mark $done "$year"
    Remove-Item $current -Force -ErrorAction SilentlyContinue
}

# Период пройден целиком — отметки больше не нужны, следующий запуск начнёт с нуля.
Remove-Item $done -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Все годы собраны и разобраны."
exit 0
