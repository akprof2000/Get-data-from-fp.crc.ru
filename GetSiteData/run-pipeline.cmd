@echo off
rem ============================================================
rem  Конвейер сбора данных с fp.crc.ru - все этапы подряд.
rem  Запускать из каталога поставки (двойным щелчком или из cmd).
rem  Настройки - в appsettings.json рядом с этим файлом.
rem  Пятый, необязательный этап (выгрузка в ClickHouse) НЕ входит
rem  в цепочку и запускается отдельно:
rem     python json_to_clickhouse.py --input-dir works/OutputJson
rem ============================================================
setlocal

rem Файл сохранён в UTF-8, поэтому на время работы переключаем кодовую страницу
rem консоли на 65001 (иначе на стандартной русской консоли 866 текст был бы
rem нечитаемым). Исходную страницу запоминаем и возвращаем в конце.
for /f "tokens=2 delims=:" %%a in ('chcp') do set "OLDCP=%%a"
chcp 65001 >nul

cd /d "%~dp0"

rem Приложения вызываем по полному пути "%~dp0<имя>.exe": в окружении может быть
rem задано NoDefaultCurrentDirectoryInExePath=1, и тогда cmd не ищет программы
rem в текущем каталоге - вызов по одному имени падал бы с "не является командой".
rem Код возврата сохраняем в RC сразу после каждого этапа: любая следующая
rem команда (в том числе set) сбрасывает errorlevel в 0.

echo.
echo ============================================
echo   Конвейер fp.crc.ru: сбор -^> JSON
echo ============================================

echo.
echo [1/4] Сбор страниц с сайта...
"%~dp0GetSiteData.exe"
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_collect

echo.
echo [2/4] Разбор HTML в тексты документов...
"%~dp0ParseHTML.exe"
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_parse

echo.
echo [3/4] Классификация: базовые станции / прочее...
"%~dp0MLTextToData.exe" process
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_ml

echo.
echo [4/4] Извлечение данных в JSON...
"%~dp0ParseTextHeader.exe"
set RC=%errorlevel%
if not "%RC%"=="0" goto :fail_extract

echo.
echo ============================================
echo   Готово. Результат:
echo     works\OutputJson\   - полные записи
echo     works\OutputErrors\ - неполные записи
echo.
echo   Выгрузить в ClickHouse (необязательно):
echo     python json_to_clickhouse.py --input-dir works/OutputJson
echo ============================================
echo.
chcp %OLDCP% >nul
exit /b 0

:fail_collect
set STEP=1/4 Сбор страниц с сайта
goto :fail
:fail_parse
set STEP=2/4 Разбор HTML
goto :fail
:fail_ml
set STEP=3/4 Классификация
goto :fail
:fail_extract
set STEP=4/4 Извлечение данных

:fail
echo.
echo ============================================
echo   ОШИБКА на этапе: %STEP% (код %RC%)
echo   Конвейер остановлен, следующие этапы не запускались.
echo ============================================
echo.
chcp %OLDCP% >nul
exit /b %RC%
