#!/usr/bin/env bash
# ============================================================
#  Конвейер сбора данных с fp.crc.ru - все этапы подряд.
#  Настройки - в appsettings.json рядом с этим файлом.
#  Пятый, необязательный этап (выгрузка в ClickHouse) НЕ входит
#  в цепочку и запускается отдельно:
#     python3 json_to_clickhouse.py --input-dir works/OutputJson
# ============================================================
set -u
cd "$(dirname "$0")"

fail() {
    echo
    echo "============================================"
    echo "  ОШИБКА на этапе: $1 (код $2)"
    echo "  Конвейер остановлен, следующие этапы не запускались."
    echo "============================================"
    echo
    exit "$2"
}

# Код возврата сохраняем в rc сразу после запуска: любая следующая команда
# (echo, [ ... ]) перезатирает $?. Конструкции вида «cmd || fail "$?"» здесь
# не используем - в них код этапа терялся и наружу уходил 0.
run_step() {
    local name="$1"
    shift
    echo
    echo "[$name]"
    "$@"
    local rc=$?
    if [ "$rc" -ne 0 ]; then
        fail "$name" "$rc"
    fi
}

echo
echo "============================================"
echo "  Конвейер fp.crc.ru: сбор -> JSON"
echo "============================================"

run_step "1/4 Сбор страниц с сайта"            ./GetSiteData
run_step "2/4 Разбор HTML в тексты документов" ./ParseHTML
run_step "3/4 Классификация"                   ./MLTextToData process
run_step "4/4 Извлечение данных в JSON"        ./ParseTextHeader

echo
echo "============================================"
echo "  Готово. Результат:"
echo "    works/OutputJson/   - полные записи"
echo "    works/OutputErrors/ - неполные записи"
echo
echo "  Выгрузить в ClickHouse (необязательно):"
echo "    python3 json_to_clickhouse.py --input-dir works/OutputJson"
echo "============================================"
echo
