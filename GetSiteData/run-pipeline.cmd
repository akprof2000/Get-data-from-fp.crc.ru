@echo off
rem ============================================================
rem  Конвейер сбора данных с fp.crc.ru - все этапы подряд.
rem  Запускать из каталога поставки (двойным щелчком или из cmd).
rem  Настройки - в appsettings.json рядом с этим файлом.
rem  Пятый, необязательный этап (выгрузка в ClickHouse) НЕ входит
rem  в цепочку и запускается отдельно:
rem     python json_to_clickhouse.py --input-dir works/OutputNormalized
rem ============================================================
setlocal

rem Файл сохранён в UTF-8, поэтому на время работы переключаем кодовую страницу
rem консоли на 65001 (иначе на стандартной русской консоли 866 текст был бы
rem нечитаемым). Исходную страницу запоминаем и возвращаем в конце.
for /f "tokens=2 delims=:" %%a in ('chcp') do set "OLDCP=%%a"
chcp 65001 >nul

cd /d "%~dp0"

rem Живой пульт: флаг-файл с именем текущего этапа + фоновый писатель, который
rem каждые 5 секунд переписывает works\pipeline-status.html (страница сама
rem обновляется), и открываем её в браузере.
if not exist "%~dp0works" mkdir "%~dp0works"
>"%~dp0works\.pipeline-running" echo collect
start "" /b powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0pipeline-status.ps1"
timeout /t 2 /nobreak >nul
start "" "%~dp0works\pipeline-status.html"

rem Приложения вызываем по полному пути "%~dp0<имя>.exe": в окружении может быть
rem задано NoDefaultCurrentDirectoryInExePath=1, и тогда cmd не ищет программы
rem в текущем каталоге - вызов по одному имени падал бы с "не является командой".
rem Код возврата сохраняем в RC сразу после каждого этапа: любая следующая
rem команда (в том числе set) сбрасывает errorlevel в 0.

echo.
echo ============================================
echo   Конвейер fp.crc.ru: сбор -^> JSON
echo ============================================

rem Этапы 1-2 ведёт pipeline-years.ps1: при периоде в один год это обычная пара
rem "сбор -> разбор", при периоде в несколько лет — погодовой цикл со сносом HTML
rem каждого года после разбора и отметками works\.years-done (перезапуск после
rem сбоя продолжает с недособранного года, а не с начала).
echo.
echo [1-2/6] Сбор страниц и разбор в тексты...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pipeline-years.ps1" -Root "%~dp0."
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_collect

echo.
echo [3/6] Классификация: базовые станции / прочее...
>"%~dp0works\.pipeline-running" echo ml
"%~dp0MLTextToData.exe" process
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_ml

echo.
echo [4/6] Извлечение данных в JSON...
>"%~dp0works\.pipeline-running" echo extract
"%~dp0ParseTextHeader.exe"
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_extract

rem Нормализация адресов по офлайн-базе ГАР. Базы нет - приложение само пытается
rem её собрать (по URL или из локальной выгрузки GarSource); собрать неоткуда -
rem этап пропускается без ошибки, конвейер продолжается.
echo.
echo [5/6] Обновление базы ГАР+OSM (при необходимости)...
>"%~dp0works\.pipeline-running" echo garupdate
"%~dp0NormalizeAddress.exe" update
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_garupdate

echo.
echo [6/6] Нормализация адресов по ГАР...
>"%~dp0works\.pipeline-running" echo normalize
"%~dp0NormalizeAddress.exe" normalize
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_normalize

echo.
echo ============================================
echo   Готово. Результат:
echo     works\OutputJson\        - все записи (у неполных заполнено поле missingFields)
echo     works\OutputNormalized\  - записи с нормализованным адресом (если есть база ГАР)
echo.
echo   Выгрузить в ClickHouse (необязательно):
echo     python json_to_clickhouse.py --input-dir works/OutputNormalized
echo ============================================
echo.
>"%~dp0works\.pipeline-running" echo done
timeout /t 6 /nobreak >nul
del "%~dp0works\.pipeline-running" >nul 2>&1
chcp %OLDCP% >nul
exit /b 0

:fail_collect
set STEP=1/6 Сбор страниц с сайта
goto :fail
:fail_parse
set STEP=2/6 Разбор HTML
goto :fail
:fail_ml
set STEP=3/6 Классификация
goto :fail
:fail_extract
set STEP=4/6 Извлечение данных
goto :fail
:fail_garupdate
set STEP=5/6 Обновление базы ГАР
goto :fail
:fail_normalize
set STEP=6/6 Нормализация адресов

:fail
echo.
echo ============================================
echo   ОШИБКА на этапе: %STEP% (код %RC%)
echo   Конвейер остановлен, следующие этапы не запускались.
echo ============================================
echo.
del "%~dp0works\.pipeline-running" >nul 2>&1
chcp %OLDCP% >nul
exit /b %RC%
