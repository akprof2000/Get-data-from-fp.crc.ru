#!/usr/bin/env bash
# Погодовой сбор (см. pipeline-years.ps1): период больше одного года собирается
# год за годом — сбор, разбор в тексты, удаление HTML этого года, отметка о
# завершении. Перезапуск продолжает с недособранного года.
set -euo pipefail
DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
# Приложения резолвят относительные пути (WorkRoot=works) от текущего каталога,
# поэтому переходим в каталог поставки.
cd "$DIR"
WORKS="$DIR/works"
DONE="$WORKS/.years-done"
CURRENT="$WORKS/.year-current"
FLAG="$WORKS/.pipeline-running"
mkdir -p "$WORKS"

# Период из appsettings.json (JSONC) с учётом переменных окружения.
cfg=$(sed 's://.*$::' "$DIR/appsettings.json")
from="${GetSiteData__Search__PeriodStart:-$(printf '%s' "$cfg" | grep -o '"PeriodStart"[^"]*"[^"]*"' | head -1 | grep -o '[0-9][0-9]\.[0-9]\{4\}')}"
to="${GetSiteData__Search__PeriodEnd:-$(printf '%s' "$cfg" | grep -o '"PeriodEnd"[^"]*"[^"]*"' | head -1 | grep -o '[0-9][0-9]\.[0-9]\{4\}')}"

from_month=$((10#${from%%.*})); from_year=${from##*.}
to_month=$((10#${to%%.*}));     to_year=${to##*.}

stage() {
    printf '%s
' "$2" > "$FLAG" 2>/dev/null || true
    local exe="$DIR/bin/$1"
    [ -x "$exe" ] || exe="$DIR/$1"      # плоская раскладка старых поставок
    "$exe" ${3:-}
}

if [ "$from_year" = "$to_year" ]; then
    stage GetSiteData collect
    stage ParseHTML   parse
    exit 0
fi

# Восстановление отметок: год с текстами и без HTML считаем собранным.
backfilled=""
cur_year=$( [ -f "$CURRENT" ] && head -1 "$CURRENT" || true )
for year in $(seq "$from_year" "$to_year"); do
    if [ -f "$DONE" ] && grep -qx "$year" "$DONE"; then continue; fi
    [ "$year" = "${cur_year:-}" ] && continue
    txt=$(find "$WORKS/documents/$year" -type f 2>/dev/null | wc -l)
    [ "$txt" -eq 0 ] && continue
    html=$(find "$WORKS/output" -mindepth 2 -maxdepth 2 -type d -name "$year" -exec find {} -type f \; 2>/dev/null | wc -l)
    [ "$html" -gt 0 ] && continue
    echo "$year" >> "$DONE"
    backfilled="$backfilled $year"
done

echo
[ -n "$backfilled" ] && echo "Найдены готовые тексты без отметок — отмечаю годы собранными:$backfilled."
echo "Период охватывает $from_year-$to_year — собираем по годам (HTML каждого года удаляется после разбора)."
[ -f "$DONE" ] && echo "Уже собраны ранее: $(tr '\n' ' ' < "$DONE")— пропускаю."

for year in $(seq "$from_year" "$to_year"); do
    if [ -f "$DONE" ] && grep -qx "$year" "$DONE"; then continue; fi

    m_from=1; m_to=12
    [ "$year" = "$from_year" ] && m_from=$from_month
    [ "$year" = "$to_year" ]   && m_to=$to_month

    echo "$year" > "$CURRENT"
    echo
    echo "===== Год $year (месяцы $m_from-$m_to) ====="

    export GetSiteData__Search__PeriodStart=$(printf '%02d.%s' "$m_from" "$year")
    export GetSiteData__Search__PeriodEnd=$(printf '%02d.%s' "$m_to" "$year")
    stage GetSiteData collect
    stage ParseHTML   parse

    # HTML этого года больше не нужен: тексты уже извлечены.
    find "$WORKS/output" -mindepth 2 -maxdepth 2 -type d -name "$year" -exec rm -rf {} + 2>/dev/null || true
    echo "Год $year: HTML удалён."

    echo "$year" >> "$DONE"
    rm -f "$CURRENT"
done

rm -f "$DONE"
echo
echo "Все годы собраны и разобраны."
