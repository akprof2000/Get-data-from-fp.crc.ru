#!/usr/bin/env python3
"""
Скрипт для рекурсивного чтения JSON-файлов и пакетной загрузки в ClickHouse.
Поддерживает многопоточность, обработку пустых полей и большие объемы данных (~600k файлов).

Изменения по сравнению с v2:
  - Полноценное логирование: ротация, JSON-формат, прогресс, отдельный error-лог
  - Логирование каждого этапа pipeline

Зависимости:
    pip install -r requirements.txt   (clickhouse-driver)

Использование:
    python json_to_clickhouse.py --input-dir OutputJson --host localhost --database sanpin --table base_stations

Реквизиты подключения можно не передавать флагами, а задать переменными окружения:
    CH_HOST, CH_PORT, CH_DATABASE, CH_TABLE, CH_USER, CH_PASSWORD
"""

import argparse
import json
import logging
import logging.handlers
import os
import re
import sys
import time
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import date, datetime
from pathlib import Path
from queue import Queue
from threading import Lock
from typing import Any, Dict, List, Optional, Tuple

from clickhouse_driver import Client
from clickhouse_driver.errors import Error as ClickHouseError

# ──────────────────────────────────────────────────────────────────────────────
# Конфигурация логирования
# ──────────────────────────────────────────────────────────────────────────────

LOG_DIR = Path("logs")
LOG_DIR.mkdir(exist_ok=True)

# Форматтеры
DETAILED_FORMATTER = logging.Formatter(
    "%(asctime)s | %(levelname)-8s | %(threadName)-15s | %(name)s | %(message)s"
)

JSON_FORMATTER = logging.Formatter(
    '{"timestamp": "%(asctime)s", "level": "%(levelname)s", "thread": "%(threadName)s", '
    '"logger": "%(name)s", "message": "%(message)s"}'
)

# Ротация: 50 МБ, храним 5 файлов
MAX_BYTES = 50 * 1024 * 1024
BACKUP_COUNT = 5


def setup_logging(json_logs: bool = False, verbose: bool = False) -> logging.Logger:
    """Настраивает полноценное логирование с ротацией."""
    logger = logging.getLogger("json_to_ch")
    logger.setLevel(logging.DEBUG if verbose else logging.INFO)
    logger.handlers = []  # очищаем старые

    # 1. Консольный handler
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setLevel(logging.DEBUG if verbose else logging.INFO)
    console_handler.setFormatter(DETAILED_FORMATTER)
    logger.addHandler(console_handler)

    # 2. Основной файл (все логи)
    main_file = LOG_DIR / "json_to_clickhouse.log"
    file_handler = logging.handlers.RotatingFileHandler(
        main_file, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8"
    )
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(JSON_FORMATTER if json_logs else DETAILED_FORMATTER)
    logger.addHandler(file_handler)

    # 3. Файл только ошибок
    error_file = LOG_DIR / "errors.log"
    error_handler = logging.handlers.RotatingFileHandler(
        error_file, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8"
    )
    error_handler.setLevel(logging.WARNING)
    error_handler.setFormatter(DETAILED_FORMATTER)
    logger.addHandler(error_handler)

    # 4. Файл парсинга (debug-уровень)
    parse_file = LOG_DIR / "parsing.log"
    parse_handler = logging.handlers.RotatingFileHandler(
        parse_file, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8"
    )
    parse_handler.setLevel(logging.DEBUG)
    parse_handler.setFormatter(DETAILED_FORMATTER)
    logger.addHandler(parse_handler)

    # 5. Файл вставок в ClickHouse
    insert_file = LOG_DIR / "inserts.log"
    insert_handler = logging.handlers.RotatingFileHandler(
        insert_file, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8"
    )
    insert_handler.setLevel(logging.DEBUG)
    insert_handler.setFormatter(DETAILED_FORMATTER)
    logger.addHandler(insert_handler)

    logger.info("Логирование настроено. Логи: %s", LOG_DIR.absolute())
    logger.info("  Основной: %s", main_file)
    logger.info("  Ошибки:   %s", error_file)
    logger.info("  Парсинг:  %s", parse_file)
    logger.info("  Вставки:  %s", insert_file)

    return logger


# ──────────────────────────────────────────────────────────────────────────────
# Конфигурация
# ──────────────────────────────────────────────────────────────────────────────
DEFAULT_BATCH_SIZE = 5000
DEFAULT_MAX_WORKERS = 8
DEFAULT_CH_WORKERS = 4
DEFAULT_QUEUE_MAXSIZE = 100

_DOC_RE = re.compile(r'^(.*?)\s+от\s+(\d{2})\.(\d{2})\.(\d{4})$')


# ──────────────────────────────────────────────────────────────────────────────
# Схема таблицы ClickHouse
# ──────────────────────────────────────────────────────────────────────────────
CREATE_TABLE_SQL = """
CREATE TABLE IF NOT EXISTS {database}.{table} (
    `index` Int64,
    document_number String,
    document_date Date,
    base_station_number Nullable(String),
    base_station_address Nullable(String),
    lat Nullable(Float64),
    lon Nullable(Float64),
    operator Nullable(String),
    processing_date DateTime,
    source_file_name String,
    relative_path String,
    raw_first_lines Array(String),
    file_path String,
    loaded_at DateTime DEFAULT now()
) ENGINE = ReplacingMergeTree()
ORDER BY (document_number, document_date)
SETTINGS index_granularity = 8192;
"""


# ──────────────────────────────────────────────────────────────────────────────
# Утилиты парсинга
# ──────────────────────────────────────────────────────────────────────────────

def parse_document_number_and_date(value: Optional[str], logger: logging.Logger) -> Tuple[Optional[str], Optional[date]]:
    """Разбивает строку вида '74.50.03.000.Т.000854.04.26 от 15.04.2026' на номер и дату."""
    if not value:
        logger.debug("documentNumberAndDate пустое")
        return None, None
    m = _DOC_RE.match(value.strip())
    if not m:
        logger.warning("Не удалось распарсить documentNumberAndDate: %s", value)
        return value.strip(), None
    doc_number = m.group(1).strip()
    try:
        doc_date = date(int(m.group(4)), int(m.group(3)), int(m.group(2)))
        logger.debug("Распарсено: номер=%s, дата=%s", doc_number, doc_date)
    except ValueError as exc:
        logger.warning("Некорректная дата в documentNumberAndDate: %s (%s)", value, exc)
        doc_date = None
    return doc_number, doc_date


def parse_coordinates(value: Optional[str], logger: logging.Logger) -> Tuple[Optional[float], Optional[float]]:
    """Парсит 'lat, lon' в два float."""
    if not value:
        logger.debug("coordinates пустые")
        return None, None
    parts = value.split(",")
    if len(parts) != 2:
        logger.warning("Некорректный формат coordinates: %s", value)
        return None, None
    try:
        lat = float(parts[0].strip())
        lon = float(parts[1].strip())
        logger.debug("Координаты: lat=%.6f, lon=%.6f", lat, lon)
        return lat, lon
    except ValueError as exc:
        logger.warning("Ошибка парсинга coordinates '%s': %s", value, exc)
        return None, None


def parse_processing_date(value: Optional[str], logger: logging.Logger) -> datetime:
    """Парсит строку даты в DateTime. Fallback на now() при ошибке."""
    if not value:
        now = datetime.now()
        logger.debug("processingDate пустое, используем now(): %s", now)
        return now
    try:
        dt_str = value.strip()
        if "+" in dt_str:
            dt_str = dt_str.rsplit("+", 1)[0]
        elif dt_str.endswith("Z"):
            dt_str = dt_str[:-1]
        if "." in dt_str:
            dt_str = dt_str.split(".")[0]
        result = datetime.strptime(dt_str, "%Y-%m-%dT%H:%M:%S")
        logger.debug("processingDate распарсен: %s", result)
        return result
    except Exception:
        for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%d.%m.%Y %H:%M:%S", "%d.%m.%Y"):
            try:
                result = datetime.strptime(value.strip()[:19], fmt)
                logger.debug("processingDate распарсен (alt fmt): %s", result)
                return result
            except Exception:
                continue
        now = datetime.now()
        logger.warning("Не удалось распарсить processingDate '%s', используем now(): %s", value, now)
        return now


def safe_int(value: Any, logger: logging.Logger) -> Optional[int]:
    """Безопасное преобразование в int."""
    if value is None:
        logger.debug("index пустой")
        return None
    try:
        result = int(value)
        logger.debug("index преобразован: %d", result)
        return result
    except (ValueError, TypeError) as exc:
        logger.warning("Не удалось преобразовать index '%s' в int: %s", value, exc)
        return None


# ──────────────────────────────────────────────────────────────────────────────
# Чтение JSON
# ──────────────────────────────────────────────────────────────────────────────

def parse_json_file(file_path: Path, logger: logging.Logger) -> Optional[Dict[str, Any]]:
    """Читает и парсит один JSON-файл."""
    logger.debug("Чтение файла: %s", file_path)
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except json.JSONDecodeError as exc:
        logger.error("JSON decode error в %s: %s", file_path, exc)
        return None
    except UnicodeDecodeError as exc:
        logger.error("Unicode error в %s: %s", file_path, exc)
        return None
    except OSError as exc:
        logger.error("OS error чтения %s: %s", file_path, exc)
        return None

    doc_number, doc_date = parse_document_number_and_date(data.get("documentNumberAndDate"), logger)
    lat, lon = parse_coordinates(data.get("coordinates"), logger)
    proc_date = parse_processing_date(data.get("processingDate"), logger)
    idx = safe_int(data.get("index"), logger)

    record = {
        "index": idx,
        "document_number": doc_number,
        "document_date": doc_date,
        "base_station_number": data.get("baseStationNumber"),
        "base_station_address": data.get("baseStationAddress"),
        "lat": lat,
        "lon": lon,
        "operator": data.get("operator"),
        "processing_date": proc_date,
        "source_file_name": str(data.get("sourceFileName", "")),
        "relative_path": str(data.get("relativePath", "")),
        "raw_first_lines": data.get("rawFirstLines") or [],
        "file_path": str(file_path),
    }
    logger.debug("Файл %s успешно распарсен (index=%s)", file_path, idx)
    return record


# Имя документа, сгенерированного нашим конвейером: «Номер заключения и дата»,
# например «01.РА.01.000.Т.000215.07.26 от 01.07.2026.json».
# Октеты номера: 2 цифры, 2 заглавные буквы/цифры (бывают кириллические — БЦ, ОМ,
# ХЦ, РА, СЦ… — и чисто цифровые — 01, 49), 2 цифры, 3 цифры, «Т» (допускаем и
# латинскую), 6 цифр, месяц, год; затем « от ДД.ММ.ГГГГ». Шаблон проверен на
# полном корпусе (111 872 документа — 100 % совпадение). Всё прочее (служебные
# _processed.json, чужие json) в загрузку не попадает.
DOCUMENT_FILE_RE = re.compile(
    r"^\d{2}\.[А-ЯЁA-Z0-9]{2}\.\d{2}\.\d{3}\.[ТT]\.\d{6}\.\d{2}\.\d{2} от \d{2}\.\d{2}\.\d{4}\.json$"
)


def collect_json_files(root_dir: Path, logger: logging.Logger) -> List[Path]:
    """Рекурсивно собирает JSON-документы конвейера (по шаблону имени)."""
    logger.info("Сканирование директории: %s", root_dir)
    all_json = list(root_dir.rglob("*.json"))
    files = [f for f in all_json if DOCUMENT_FILE_RE.match(f.name)]
    skipped = len(all_json) - len(files)
    logger.info("Найдено JSON-файлов: %d", len(files))
    if skipped:
        logger.info("Пропущено не-документов (служебные/посторонние json): %d", skipped)
    if len(files) > 0:
        logger.debug("Примеры файлов: %s", [str(f) for f in files[:5]])
    return files


# ──────────────────────────────────────────────────────────────────────────────
# ClickHouse клиент
# ──────────────────────────────────────────────────────────────────────────────

class ClickHouseWriter:
    INSERT_SQL = """
    INSERT INTO {database}.{table}
    (`index`, document_number, document_date, base_station_number, base_station_address,
     lat, lon, operator, processing_date, source_file_name, relative_path,
     raw_first_lines, file_path)
    VALUES
    """

    def __init__(
        self,
        host: str,
        port: int,
        database: str,
        table: str,
        user: str,
        password: str,
        batch_size: int,
        logger: logging.Logger,
    ):
        self.host = host
        self.port = port
        self.database = database
        self.table = table
        self.user = user
        self.password = password
        self.batch_size = batch_size
        self.logger = logger
        self.insert_sql = self.INSERT_SQL.format(database=database, table=table)
        self._lock = Lock()
        self._client: Optional[Client] = None
        self._total_inserted = 0
        self._total_batches = 0

    def _get_client(self) -> Client:
        if self._client is None:
            self.logger.info("Подключение к ClickHouse: %s:%d/%s", self.host, self.port, self.database)
            try:
                self._client = Client(
                    host=self.host,
                    port=self.port,
                    database=self.database,
                    user=self.user,
                    password=self.password,
                    settings={
                        "max_execution_time": 300,
                        "max_insert_block_size": self.batch_size * 2,
                    },
                )
                self.logger.info("Подключение установлено")
            except Exception as exc:
                self.logger.error("Ошибка подключения к ClickHouse: %s", exc)
                raise
        return self._client

    def ensure_table(self) -> None:
        sql = CREATE_TABLE_SQL.format(database=self.database, table=self.table)
        self.logger.info("Создание/проверка таблицы %s.%s", self.database, self.table)
        try:
            self._get_client().execute(sql)
            self.logger.info("Таблица готова")
        except Exception as exc:
            self.logger.error("Ошибка создания таблицы: %s", exc)
            raise

    def insert_batch(self, rows: List[Dict[str, Any]]) -> int:
        if not rows:
            return 0

        self.logger.debug("Вставка batch: %d строк", len(rows))

        data = [
            (
                r["index"],
                r["document_number"],
                r["document_date"],
                r["base_station_number"],
                r["base_station_address"],
                r["lat"],
                r["lon"],
                r["operator"],
                r["processing_date"],
                r["source_file_name"],
                r["relative_path"],
                r["raw_first_lines"],
                r["file_path"],
            )
            for r in rows
        ]

        try:
            with self._lock:
                self._get_client().execute(self.insert_sql, data)
            self._total_inserted += len(rows)
            self._total_batches += 1
            self.logger.debug("Batch вставлен: %d строк (всего: %d)", len(rows), self._total_inserted)
            return len(rows)
        except ClickHouseError as exc:
            self.logger.error("Ошибка batch insert (%d строк): %s", len(rows), exc)
            # Fallback: построчная вставка
            success = 0
            for i, row in enumerate(data):
                try:
                    with self._lock:
                        self._get_client().execute(self.insert_sql, [row])
                    success += 1
                except ClickHouseError as e2:
                    self.logger.error("Ошибка вставки строки %d: %s", i, e2)
            self._total_inserted += success
            self.logger.warning("Fallback insert: %d/%d строк успешно", success, len(rows))
            return success


# ──────────────────────────────────────────────────────────────────────────────
# Прогресс-репортер
# ──────────────────────────────────────────────────────────────────────────────

class ProgressReporter:
    def __init__(self, logger: logging.Logger, total_files: int, report_interval: float = 10.0):
        self.logger = logger
        self.total_files = total_files
        self.report_interval = report_interval
        self.start_time = time.time()
        self.last_report_time = self.start_time
        self.last_parsed = 0
        self.last_inserted = 0
        self._lock = Lock()

    def report(self, parsed: int, inserted: int, errors: int) -> None:
        now = time.time()
        with self._lock:
            if now - self.last_report_time < self.report_interval:
                return
            elapsed = now - self.start_time
            delta = now - self.last_report_time

            files_per_sec = (parsed + errors - self.last_parsed) / delta if delta > 0 else 0
            recs_per_sec = (inserted - self.last_inserted) / delta if delta > 0 else 0
            total_rate = inserted / elapsed if elapsed > 0 else 0
            progress_pct = (parsed + errors) / self.total_files * 100 if self.total_files > 0 else 0

            self.logger.info(
                "Прогресс: %.1f%% | Файлов: %d/%d | Записей: %d | "
                "Ошибок: %d | Скорость: %.1f файлов/с, %.1f записей/с | "
                "Средняя: %.1f записей/с | Прошло: %.1f с",
                progress_pct, parsed + errors, self.total_files, inserted,
                errors, files_per_sec, recs_per_sec, total_rate, elapsed
            )

            self.last_report_time = now
            self.last_parsed = parsed + errors
            self.last_inserted = inserted

    def final(self, parsed: int, inserted: int, errors: int) -> None:
        elapsed = time.time() - self.start_time
        self.logger.info("=" * 60)
        self.logger.info("ИТОГОВЫЙ ОТЧЁТ")
        self.logger.info("=" * 60)
        self.logger.info("Всего файлов:     %d", self.total_files)
        self.logger.info("Распарсено:       %d", parsed)
        self.logger.info("Ошибок парсинга:  %d", errors)
        self.logger.info("Вставлено в CH:   %d", inserted)
        self.logger.info("Пропущено:        %d", parsed - inserted)
        self.logger.info("Время:            %.2f сек", elapsed)
        self.logger.info("Средняя скорость: %.1f записей/с", inserted / elapsed if elapsed > 0 else 0)
        self.logger.info("=" * 60)


# ──────────────────────────────────────────────────────────────────────────────
# Производитель (чтение файлов)
# ──────────────────────────────────────────────────────────────────────────────

def file_reader_worker(
    file_paths: List[Path],
    queue: Queue,
    stats: Dict[str, int],
    stats_lock: Lock,
    logger: logging.Logger,
) -> None:
    logger.info("Поток чтения запущен, файлов: %d", len(file_paths))
    for fp in file_paths:
        record = parse_json_file(fp, logger)
        if record is not None:
            queue.put(record)
            with stats_lock:
                stats["parsed"] += 1
        else:
            with stats_lock:
                stats["errors"] += 1
    logger.info("Поток чтения завершён, обработано: %d", len(file_paths))


# ──────────────────────────────────────────────────────────────────────────────
# Потребитель (запись в ClickHouse)
# ──────────────────────────────────────────────────────────────────────────────

def clickhouse_writer_worker(
    queue: Queue,
    writer: ClickHouseWriter,
    stats: Dict[str, int],
    stats_lock: Lock,
    stop_event: List[bool],
    logger: logging.Logger,
) -> None:
    logger.info("Поток записи в ClickHouse запущен")
    batch: List[Dict[str, Any]] = []
    while not stop_event[0] or not queue.empty():
        try:
            record = queue.get(timeout=1.0)
        except Exception:
            continue

        batch.append(record)

        if len(batch) >= writer.batch_size:
            inserted = writer.insert_batch(batch)
            with stats_lock:
                stats["inserted"] += inserted
            batch = []

    if batch:
        inserted = writer.insert_batch(batch)
        with stats_lock:
            stats["inserted"] += inserted

    logger.info("Поток записи завершён, всего вставлено: %d", writer._total_inserted)


# ──────────────────────────────────────────────────────────────────────────────
# Главная функция
# ──────────────────────────────────────────────────────────────────────────────

def run(
    input_dir: Path,
    host: str,
    port: int,
    database: str,
    table: str,
    user: str,
    password: str,
    batch_size: int,
    max_workers: int,
    ch_workers: int,
    json_logs: bool,
    verbose: bool,
) -> None:
    logger = setup_logging(json_logs=json_logs, verbose=verbose)
    logger.info("=" * 60)
    logger.info("СТАРТ ЗАГРУЗКИ JSON -> ClickHouse")
    logger.info("=" * 60)
    logger.info("Параметры:")
    logger.info("  input_dir:   %s", input_dir)
    logger.info("  host:        %s:%d", host, port)
    logger.info("  database:    %s", database)
    logger.info("  table:       %s", table)
    logger.info("  batch_size:  %d", batch_size)
    logger.info("  workers:     %d (чтение) + %d (запись)", max_workers, ch_workers)

    # 1. Сканирование
    files = collect_json_files(input_dir, logger)
    total_files = len(files)

    if total_files == 0:
        logger.warning("Файлы не найдены. Завершение.")
        return

    # 2. ClickHouse
    writer = ClickHouseWriter(host, port, database, table, user, password, batch_size, logger)
    writer.ensure_table()

    # 3. Статистика + прогресс
    stats = {"parsed": 0, "errors": 0, "inserted": 0}
    stats_lock = Lock()
    reporter = ProgressReporter(logger, total_files, report_interval=5.0)

    # 4. Очередь
    queue: Queue = Queue(maxsize=DEFAULT_QUEUE_MAXSIZE)
    stop_event = [False]

    # 5. Распределение файлов
    chunk_size = max(1, total_files // max_workers)
    file_chunks = [files[i : i + chunk_size] for i in range(0, total_files, chunk_size)]
    logger.info("Файлы распределены на %d chunks", len(file_chunks))

    # 6. Запуск потоков
    start_time = time.time()
    with ThreadPoolExecutor(max_workers=max_workers + ch_workers) as executor:
        reader_futures = [
            executor.submit(file_reader_worker, chunk, queue, stats, stats_lock, logger)
            for chunk in file_chunks
        ]

        writer_futures = [
            executor.submit(clickhouse_writer_worker, queue, writer, stats, stats_lock, stop_event, logger)
            for _ in range(ch_workers)
        ]

        # Мониторинг прогресса
        all_done_readers = False
        while not all_done_readers:
            time.sleep(1.0)
            with stats_lock:
                reporter.report(stats["parsed"], stats["inserted"], stats["errors"])
            all_done_readers = all(f.done() for f in reader_futures)

        # Ждем читателей
        for future in as_completed(reader_futures):
            try:
                future.result()
            except Exception as exc:
                logger.error("Ошибка в потоке чтения: %s\n%s", exc, traceback.format_exc())

        logger.info("Все файлы прочитаны. Ожидание записи...")
        stop_event[0] = True

        # Ждем писателей
        for future in as_completed(writer_futures):
            try:
                future.result()
            except Exception as exc:
                logger.error("Ошибка в потоке записи: %s\n%s", exc, traceback.format_exc())

    # Итог
    with stats_lock:
        reporter.final(stats["parsed"], stats["inserted"], stats["errors"])


# ──────────────────────────────────────────────────────────────────────────────
# CLI
# ──────────────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Загрузка JSON-файлов в ClickHouse с полноценным логированием"
    )
    # По умолчанию — выход ParseTextHeader под общим корнем рабочих данных конвейера.
    parser.add_argument("--input-dir", default="works/OutputJson", help="Корневая директория с JSON")
    # Подключение: значения по умолчанию берутся из переменных окружения CH_*,
    # чтобы реальные адреса и пароли не попадали в код и историю git.
    parser.add_argument("--host", default=os.environ.get("CH_HOST", "localhost"), help="ClickHouse host")
    parser.add_argument("--port", type=int, default=int(os.environ.get("CH_PORT", "9000")), help="ClickHouse native port")
    parser.add_argument("--database", default=os.environ.get("CH_DATABASE", "sanpin"), help="База данных")
    parser.add_argument("--table", default=os.environ.get("CH_TABLE", "base_stations"), help="Имя таблицы")
    parser.add_argument("--user", default=os.environ.get("CH_USER", "default"), help="Пользователь")
    parser.add_argument("--password", default=os.environ.get("CH_PASSWORD", ""), help="Пароль")
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE)
    parser.add_argument("--workers", type=int, default=DEFAULT_MAX_WORKERS)
    parser.add_argument("--ch-workers", type=int, default=DEFAULT_CH_WORKERS)
    parser.add_argument("--json-logs", action="store_true", help="JSON-формат логов")
    parser.add_argument("--verbose", "-v", action="store_true", help="DEBUG-уровень в консоль")

    args = parser.parse_args()

    run(
        input_dir=Path(args.input_dir),
        host=args.host,
        port=args.port,
        database=args.database,
        table=args.table,
        user=args.user,
        password=args.password,
        batch_size=args.batch_size,
        max_workers=args.workers,
        ch_workers=args.ch_workers,
        json_logs=args.json_logs,
        verbose=args.verbose,
    )


if __name__ == "__main__":
    main()
