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

function Invoke-Stage($exe, $stageName, $arguments) {
    Set-Content -Path $flag -Value $stageName -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($arguments) { & (Join-Path $Root $exe) $arguments } else { & (Join-Path $Root $exe) }
    if ($LASTEXITCODE -ne 0) { throw "$exe завершился с кодом $LASTEXITCODE" }
}

if ($fromYear -eq $toYear) {
    # Один год — обычный порядок, HTML не удаляем (может пригодиться для отладки).
    Invoke-Stage "GetSiteData.exe" "collect" $null
    Invoke-Stage "ParseHTML.exe"   "parse"   $null
    exit 0
}

$doneYears = @()
if (Test-Path $done) { $doneYears = @(Get-Content $done | Where-Object { $_ -match '^\d{4}$' }) }

Write-Host ""
Write-Host "Период охватывает $fromYear-$toYear — собираем по годам (HTML каждого года удаляется после разбора)."
if ($doneYears.Count -gt 0) { Write-Host "Уже собраны ранее: $($doneYears -join ', ') — пропускаю." }

foreach ($year in $fromYear..$toYear) {
    if ($doneYears -contains "$year") { continue }

    # Внутри года период ограничен только на краях диапазона.
    $mFrom = if ($year -eq $fromYear) { $fromMonth } else { 1 }
    $mTo   = if ($year -eq $toYear)   { $toMonth }   else { 12 }

    Set-Content -Path $current -Value "$year" -Encoding UTF8
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

    Add-Content -Path $done -Value "$year" -Encoding UTF8
    Remove-Item $current -Force -ErrorAction SilentlyContinue
}

# Период пройден целиком — отметки больше не нужны, следующий запуск начнёт с нуля.
Remove-Item $done -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Все годы собраны и разобраны."
exit 0
