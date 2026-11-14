"""Short, serialized SQLite transactions run outside the event loop, before effects."""

import asyncio
import hashlib
import json
import os
import shutil
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from threading import RLock

from pydantic import ValidationError

from . import persisted
from .contracts import (
    EVENT_ADAPTER,
    OBSERVATION_TOOLS,
    TERMINAL_STATUSES,
    AcceptedRun,
    AgentError,
    EventState,
    PreparedOperation,
    RunSnapshot,
    SafetyBarrier,
    command_is_observation,
    new_id,
    utc_now,
)
from .owned_stdio import atomic_json

SCHEMA_VERSION = 1
SCHEMA = """
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE conversations(id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE requests(id TEXT PRIMARY KEY, fingerprint TEXT, run_id TEXT, stop_requested INTEGER NOT NULL DEFAULT 0);
CREATE TABLE runs(id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, conversation_id TEXT NOT NULL,
 command TEXT NOT NULL, snapshot TEXT NOT NULL, unfinished INTEGER NOT NULL);
CREATE UNIQUE INDEX one_unfinished_run ON runs(unfinished) WHERE unfinished=1;
CREATE TABLE events(run_id TEXT NOT NULL, seq INTEGER NOT NULL, payload TEXT NOT NULL,
 PRIMARY KEY(run_id, seq), FOREIGN KEY(run_id) REFERENCES runs(id));
CREATE TABLE messages(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, run_id TEXT,
 role TEXT NOT NULL, text TEXT NOT NULL, model_message TEXT, at TEXT NOT NULL);
CREATE INDEX message_conversation ON messages(conversation_id, at);
CREATE TABLE operations(id TEXT PRIMARY KEY, run_id TEXT NOT NULL, prepared TEXT NOT NULL,
 state TEXT NOT NULL, job_id TEXT, report TEXT, result TEXT, FOREIGN KEY(run_id) REFERENCES runs(id));
CREATE TABLE approvals(id TEXT PRIMARY KEY, run_id TEXT NOT NULL, digest TEXT NOT NULL, state TEXT NOT NULL);
CREATE TABLE assets(id TEXT PRIMARY KEY, kind TEXT NOT NULL, metadata TEXT NOT NULL);
CREATE TABLE reviews(id TEXT PRIMARY KEY, payload TEXT NOT NULL, at TEXT NOT NULL);
PRAGMA user_version=1;
"""


def canonical(value):
    if hasattr(value, "model_dump"):
        value = value.model_dump(mode="json")
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def fingerprint(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


class Store:
    def __init__(self, directory: Path, *, quota_bytes=2 * 1024**3):
        self.directory = Path(directory)
        self.path = self.directory / "history.sqlite3"
        self.quota_bytes = quota_bytes
        self._lock = RLock()
        self._connection = None
        self.failed = False
        self.known_jobs = {}

    async def open(self):
        def opening():
            from .assets import assert_no_links
            with self._lock:
                assert_no_links(self.directory)
                self.directory.mkdir(parents=True, exist_ok=True)
                assert_no_links(self.path)
                existed = self.path.exists()
                connection = None
                try:
                    connection = sqlite3.connect(self.path, timeout=5, check_same_thread=False,
                                                 isolation_level=None)
                    connection.row_factory = sqlite3.Row
                    if connection.execute("PRAGMA quick_check").fetchone()[0] != "ok":
                        raise sqlite3.DatabaseError()
                    current = connection.execute("PRAGMA user_version").fetchone()[0]
                    if existed and current != SCHEMA_VERSION:
                        raise sqlite3.DatabaseError()
                    if not existed:
                        connection.executescript("BEGIN IMMEDIATE;\n" + SCHEMA + "\nCOMMIT;")
                    connection.execute("PRAGMA journal_mode=WAL")
                    connection.execute("PRAGMA synchronous=FULL")
                    connection.execute("PRAGMA foreign_keys=ON")
                    connection.execute("SELECT id FROM requests LIMIT 1").fetchall()
                    self._connection, self.failed = connection, False
                except (sqlite3.Error, OSError):
                    if connection:
                        connection.close()
                    self.failed = True
                    raise AgentError("STORE_UNAVAILABLE", "Хранилище повреждено или несовместимо; исходные данные сохранены.", 503) from None
        await asyncio.to_thread(opening)

    async def aclose(self):
        def closing():
            with self._lock:
                if self._connection:
                    self._connection.close()
                self._connection = None
        await asyncio.to_thread(closing)

    def _backup_sync(self):
        target_dir = self.directory / "backups"
        from .assets import assert_no_links
        assert_no_links(target_dir)
        target_dir.mkdir(parents=True, exist_ok=True)
        assert_no_links(target_dir)
        destination = target_dir / f"history-{new_id()}.sqlite3"
        backup_db = sqlite3.connect(destination)
        try:
            with self._lock:
                if self._connection is None:
                    raise AgentError("STORE_UNAVAILABLE", "Хранилище недоступно.", 503)
                self._connection.backup(backup_db)
            backup_db.execute("PRAGMA wal_checkpoint(PASSIVE)")
            backup_db.commit()
        finally:
            backup_db.close()
        return destination

    async def backup(self):
        try:
            return await asyncio.to_thread(self._backup_sync)
        except AgentError:
            raise
        except (OSError, sqlite3.Error):
            raise AgentError("BACKUP_UNAVAILABLE", "Резервная копия не создана; исходное хранилище сохранено.", 503) from None

    @contextmanager
    def _transaction(self, db):
        db.execute("BEGIN IMMEDIATE")
        try:
            yield
            db.execute("COMMIT")
        except BaseException:
            db.execute("ROLLBACK")
            raise

    async def _db(self, action, *, write=False):
        def execute():
            with self._lock:
                if self._connection is None or (write and self.failed):
                    raise AgentError("STORE_UNAVAILABLE", "Хранилище недоступно; новые действия запрещены.", 503)
                try:
                    if write:
                        with self._transaction(self._connection):
                            return action(self._connection)
                    return action(self._connection)
                except (sqlite3.Error, OSError, ValidationError, ValueError, TypeError):
                    self.failed = True
                    raise AgentError("STORE_UNAVAILABLE", "Не удалось прочитать или сохранить durable state.", 503) from None
        return await asyncio.to_thread(execute)

    def _snapshot(self, db, run_id):
        row = db.execute("SELECT snapshot FROM runs WHERE id=?", (run_id,)).fetchone()
        if row is None:
            raise AgentError("RUN_NOT_FOUND", "Запуск не найден.", 404, retryable=True)
        return persisted.snapshot(json.loads(row[0]), self._workspace(db))

    @staticmethod
    def _workspace(db):
        row = db.execute("SELECT value FROM meta WHERE key='workspace'").fetchone()
        return json.loads(row[0]) if row else {}

    async def get_workspace(self):
        return await self._db(lambda db: persisted.workspace(self._workspace(db)))

    def _save_snapshot(self, db, snapshot):
        db.execute("UPDATE runs SET snapshot=?, unfinished=? WHERE id=?", (
            snapshot.model_dump_json(), int(snapshot.status not in TERMINAL_STATUSES), snapshot.id,
        ))

    def _event(self, db, snapshot, event_type, data, changes=None):
        values = {**snapshot.model_dump(), **(changes or {}), "last_seq": snapshot.last_seq + 1,
                  "updated_at": utc_now()}
        snapshot = RunSnapshot.model_validate(values)
        state = EventState.model_validate({key: values[key] for key in EventState.model_fields})
        event = EVENT_ADAPTER.validate_python(dict(run_id=snapshot.id, seq=snapshot.last_seq,
                                                   type=event_type, data=data, state=state))
        db.execute("INSERT INTO events VALUES(?,?,?)", (snapshot.id, event.seq, event.model_dump_json()))
        self._save_snapshot(db, snapshot)
        return event, snapshot

    def _duplicate(self, db, command):
        row = db.execute("SELECT fingerprint, run_id FROM requests WHERE id=?",
                         (command.client_request_id,)).fetchone()
        if row and row["run_id"]:
            if row["fingerprint"] != fingerprint(command):
                raise AgentError("REQUEST_ID_CONFLICT", "Этот ID команды уже связан с другим содержимым.")
            return AcceptedRun(is_new=False, snapshot=self._snapshot(db, row["run_id"]))
        return None

    async def lookup_command(self, command):
        return await self._db(lambda db: self._duplicate(db, command))

    def _storage(self):
        from .assets import assert_no_links
        total = 0
        for root, directories, files in os.walk(self.directory, followlinks=False):
            for name in [*directories, *files]:
                assert_no_links(Path(root) / name)
            total += sum((Path(root) / name).stat().st_size for name in files)
        free = shutil.disk_usage(self.directory).free
        return {"bytes": total, "free_bytes": free, "quota_bytes": self.quota_bytes,
                "warning": total >= self.quota_bytes * 0.8,
                "full": total >= self.quota_bytes or free < 32 * 1024**2}

    async def storage_state(self):
        return await asyncio.to_thread(self._storage)

    def _barrier(self, db):
        row = db.execute("SELECT value FROM meta WHERE key='safety_barrier'").fetchone()
        return SafetyBarrier.model_validate_json(row[0]) if row else None

    async def accept_run(self, command, profile_snapshot, *, output_directory=None):
        def accept(db):
            duplicate = self._duplicate(db, command)
            if duplicate:
                return duplicate
            row = db.execute("SELECT stop_requested FROM requests WHERE id=?",
                             (command.client_request_id,)).fetchone()
            early_stop = bool(row and row[0])
            if not early_stop:
                if db.execute("SELECT id FROM runs WHERE unfinished=1").fetchone():
                    raise AgentError("BUSY", "Другой запуск ещё не завершён.")
                if not command_is_observation(command) and self._barrier(db):
                    raise AgentError("SAFETY_BARRIER", "Прежний исход или состояние исполнителя не подтверждены; изменения недоступны.")
                if not command_is_observation(command) and self._storage()["full"]:
                    raise AgentError("STORAGE_FULL", "Лимит хранилища исчерпан; новый изменяющий запуск запрещён.", 503)
            snapshot = RunSnapshot(id=new_id(), client_request_id=command.client_request_id,
                conversation_id=command.conversation_id, kind=command.kind,
                profile=profile_snapshot, document_id=command.document_id, safety_barrier=self._barrier(db),
                output_directory=output_directory if output_directory is not None else command.output_directory,
                max_calls=command.max_calls,
                stop_requested=early_stop, status="stopped" if early_stop else "preparing")
            db.execute("INSERT OR IGNORE INTO conversations VALUES(?,?,?)", (
                command.conversation_id, command.message[:80] or "Новая переписка", snapshot.created_at,
            ))
            db.execute("INSERT INTO runs VALUES(?,?,?,?,?,?)", (
                snapshot.id, command.client_request_id, command.conversation_id,
                canonical(command), snapshot.model_dump_json(), int(not early_stop),
            ))
            db.execute("INSERT INTO requests VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET fingerprint=excluded.fingerprint, run_id=excluded.run_id", (
                command.client_request_id, fingerprint(command), snapshot.id, int(early_stop),
            ))
            _, snapshot = self._event(db, snapshot, "status", {"status": snapshot.status})
            if command.message:
                _, snapshot = self._message(db, snapshot, "user", command.message)
            return AcceptedRun(is_new=True, snapshot=snapshot)
        return await self._db(accept, write=True)

    async def get_snapshot(self, run_id):
        return await self._db(lambda db: self._snapshot(db, run_id))

    async def get_command(self, run_id):
        return await self._db(lambda db: persisted.command(json.loads(
            db.execute("SELECT command FROM runs WHERE id=?", (run_id,)).fetchone()[0]), self._workspace(db)))

    @staticmethod
    def page(offset, limit):
        if not isinstance(offset, int) or not isinstance(limit, int) or offset < 0 or not 1 <= limit <= 500:
            raise AgentError("PAGE_INVALID", "Неверные границы страницы.", 422)

    async def list_runs(self, *, offset=0, limit=100, conversation_id=None):
        self.page(offset, limit)
        def query(db):
            rows = db.execute("SELECT snapshot FROM runs WHERE (? IS NULL OR conversation_id=?) ORDER BY rowid DESC LIMIT ? OFFSET ?",
                              (conversation_id, conversation_id, limit, offset)).fetchall()
            return [persisted.snapshot(json.loads(row[0]), self._workspace(db)) for row in rows]
        return await self._db(query)

    async def get_request(self, request_id):
        def query(db):
            row = db.execute("SELECT run_id FROM requests WHERE id=?", (request_id,)).fetchone()
            return self._snapshot(db, row[0]) if row and row[0] else None
        return await self._db(query)

    async def is_stop_requested(self, request_id):
        def query(db):
            row = db.execute("SELECT stop_requested FROM requests WHERE id=?", (request_id,)).fetchone()
            return bool(row and row[0])
        return await self._db(query)

    async def stop_request(self, request_id):
        def stop(db):
            db.execute("INSERT INTO requests(id,stop_requested) VALUES(?,1) ON CONFLICT(id) DO UPDATE SET stop_requested=1", (request_id,))
            row = db.execute("SELECT run_id FROM requests WHERE id=?", (request_id,)).fetchone()
            if row[0] is None:
                return None
            snapshot = self._snapshot(db, row[0])
            status = snapshot.status if snapshot.status in TERMINAL_STATUSES else "stopping"
            db.execute("UPDATE approvals SET state='invalidated' WHERE run_id=? AND state='pending'", (snapshot.id,))
            _, snapshot = self._event(db, snapshot, "status", {"status": status}, {
                "stop_requested": True, "status": status,
            })
            return snapshot
        return await self._db(stop, write=True)

    async def append_event(self, run_id, event_type, data, state=None):
        return await self._db(lambda db: self._event(db, self._snapshot(db, run_id),
                              event_type, data, state)[0], write=True)

    async def set_status(self, run_id, status, **changes):
        return await self._db(lambda db: self._event(db, self._snapshot(db, run_id), "status",
                              {"status": status}, {**changes, "status": status})[1], write=True)

    async def get_events(self, run_id, *, after=0, limit=100):
        self.page(after, limit)
        def query(db):
            self._snapshot(db, run_id)
            rows = db.execute("SELECT payload FROM events WHERE run_id=? AND seq>? ORDER BY seq LIMIT ?",
                              (run_id, after, limit)).fetchall()
            return [persisted.event(json.loads(row[0])) for row in rows]
        return await self._db(query)

    def _message(self, db, snapshot, role, text, model_message=None):
        message = {"id": new_id(), "role": role, "text": text}
        db.execute("INSERT INTO messages VALUES(?,?,?,?,?,?,?)", (
            message["id"], snapshot.conversation_id, snapshot.id, role, text,
            canonical(model_message) if model_message else None, utc_now(),
        ))
        return self._event(db, snapshot, "message", message)

    async def add_message(self, run_id, role, text, model_message=None):
        return await self._db(lambda db: self._message(db, self._snapshot(db, run_id), role,
                              text, model_message)[0], write=True)

    async def get_messages(self, conversation_id, *, offset=0, limit=100):
        self.page(offset, limit)
        return await self._db(lambda db: [dict(row) for row in db.execute(
            "SELECT id,run_id,role,text,at FROM messages WHERE conversation_id=? ORDER BY rowid LIMIT ? OFFSET ?",
            (conversation_id, limit, offset)).fetchall()])

    async def previous_dialog(self, conversation_id, current_run):
        def query(db):
            rows = db.execute("SELECT role,text FROM messages WHERE conversation_id=? AND run_id<>? AND role IN ('user','assistant') ORDER BY rowid DESC LIMIT 100",
                              (conversation_id, current_run)).fetchall()
            dialog, size = [], 0
            for row in rows:
                size += len(row['text'].encode('utf-8'))
                if size > 256 * 1024:
                    break
                if row['text']:
                    dialog.append({'role': row['role'], 'content': row['text']})
            return list(reversed(dialog))
        return await self._db(query)

    async def create_conversation(self, title="Новая переписка"):
        item = {"id": new_id(), "title": title, "created_at": utc_now()}
        await self._db(lambda db: db.execute("INSERT INTO conversations VALUES(?,?,?)",
                       tuple(item.values())), write=True)
        return item

    async def list_conversations(self, *, offset=0, limit=100):
        self.page(offset, limit)
        return await self._db(lambda db: [dict(row) for row in db.execute(
            "SELECT * FROM conversations ORDER BY rowid DESC LIMIT ? OFFSET ?", (limit, offset))])

    async def record_operation(self, operation):
        def record(db):
            snapshot = self._snapshot(db, operation.run_id)
            if snapshot.status in TERMINAL_STATUSES or snapshot.stop_requested:
                raise AgentError("STOP_REQUESTED", "Следующие действия остановлены.")
            db.execute("INSERT INTO operations(id,run_id,prepared,state) VALUES(?,?,?,'prepared')",
                       (operation.operation_id, operation.run_id, operation.model_dump_json()))
            return self._event(db, snapshot, "tool_proposed", {"operation": operation},
                               {"active_operation_id": operation.operation_id, "job_id": None})[0]
        return await self._db(record, write=True)

    async def mark_dispatching(self, operation_id, *, expected=None):
        def dispatch(db):
            from .policy import MVP_TOOLS, POLICY_VERSION, Policy
            row = db.execute("SELECT * FROM operations WHERE id=?", (operation_id,)).fetchone()
            if not row:
                raise AgentError('OPERATION_NOT_FOUND', 'Подготовленная операция не найдена.', 404)
            snapshot = self._snapshot(db, row["run_id"])
            if row["state"] != "prepared" or snapshot.stop_requested or snapshot.status in TERMINAL_STATUSES:
                raise AgentError("DISPATCH_BLOCKED", "Операция остановлена либо Run завершён.")
            prepared = PreparedOperation.model_validate_json(row['prepared'])
            if (prepared.operation_id != operation_id or prepared.run_id != snapshot.id
                    or prepared.tool not in MVP_TOOLS or prepared.policy_version != POLICY_VERSION or prepared.digest != Policy.operation_digest(prepared)
                    or (expected is not None and prepared != expected)):
                raise AgentError('OPERATION_CHANGED', 'Сохранённая операция изменилась или относится к старой версии; отправка запрещена.')
            if expected is not None:
                context = db.execute('SELECT value FROM meta WHERE key=?', (f'context:{snapshot.id}',)).fetchone()
                if not context or prepared.scope_digest != fingerprint(persisted.context(json.loads(context[0]), self._workspace(db))):
                    raise AgentError('SCOPE_CHANGED', 'Зафиксированный контекст изменился до отправки.')
            if prepared.tool not in OBSERVATION_TOOLS and self._barrier(db):
                raise AgentError('SAFETY_BARRIER', 'Состояние исполнения не подтверждено; новая мутация не отправлена.')
            if snapshot.counters.calls + 1 + snapshot.counters.reserved_postchecks > snapshot.max_calls:
                raise AgentError("CALL_LIMIT", "Исчерпан бюджет вызовов.")
            db.execute("UPDATE operations SET state='dispatching' WHERE id=?", (operation_id,))
            counters = snapshot.counters.model_copy(update={"calls": snapshot.counters.calls + 1})
            return self._event(db, snapshot, "tool_start", {
                "operation_id": operation_id, "tool": prepared.tool,
            }, {"counters": counters})[0]
        return await self._db(dispatch, write=True)

    async def record_job(self, update):
        self.known_jobs[update.operation_id] = update.job_id
        def record(db):
            row = db.execute("SELECT run_id,job_id FROM operations WHERE id=?", (update.operation_id,)).fetchone()
            if row["job_id"] and row["job_id"] != update.job_id:
                raise AgentError("JOB_BINDING_CONFLICT", "Получен другой job_id.", 503)
            db.execute("UPDATE operations SET state='known_job', job_id=?, report=? WHERE id=?",
                       (update.job_id, canonical(update.report), update.operation_id))
            snapshot = self._snapshot(db, row["run_id"])
            return self._event(db, snapshot, "job_progress", {
                "operation_id": update.operation_id, "job_id": update.job_id,
                "status": update.status, "stage": update.stage,
            }, {"job_id": update.job_id, "monitoring_state": update.monitoring_state})[0]
        try:
            return await self._db(record, write=True)
        except AgentError:
            await self.emergency(update.operation_id, update.model_dump())
            raise

    async def emergency(self, operation_id, data):
        def write():
            directory = self.directory / "emergency"
            directory.mkdir(exist_ok=True)
            atomic_json(directory / f"{operation_id}.json", data)
        try:
            await asyncio.to_thread(write)
        except OSError:
            pass  # Known job binding also remains in memory; never permit a replay.

    async def complete_operation(self, result):
        def complete(db):
            row = db.execute("SELECT run_id FROM operations WHERE id=?", (result.operation_id,)).fetchone()
            db.execute("UPDATE operations SET state='terminal', job_id=?, report=?, result=? WHERE id=?",
                       (result.job_id, canonical(result.report), result.model_dump_json(), result.operation_id))
            snapshot = self._snapshot(db, row[0])
            unsafe = result.outcome == "unknown" or (result.job_id is not None and
                (not result.cleanup or result.cleanup.get("completed") is not True))
            changes = {"active_operation_id": None}
            if unsafe:
                barrier = SafetyBarrier(reason="COREL_OUTCOME_UNKNOWN" if result.outcome == "unknown" else "CLEANUP_NOT_PROVEN",
                    run_id=snapshot.id, operation_id=result.operation_id, job_id=result.job_id)
                self._set_barrier(db, barrier)
                changes.update(safety_barrier=barrier, status="uncertain" if result.outcome == "unknown" else "halted")
            return self._event(db, snapshot, "tool_result", {
                "operation_id": result.operation_id, "outcome": result.outcome, "applied": result.applied,
                "cleanup": result.cleanup, "error": result.report.get("error"),
            }, changes)[0]
        try:
            return await self._db(complete, write=True)
        except AgentError:
            await self.emergency(result.operation_id, result.model_dump())
            raise

    async def get_operation(self, operation_id):
        def query(db):
            row = db.execute("SELECT * FROM operations WHERE id=?", (operation_id,)).fetchone()
            if not row:
                raise AgentError("OPERATION_NOT_FOUND", "Операция не найдена.", 404)
            return {key: json.loads(row[key]) if row[key] and key in {"prepared", "report", "result"}
                    else row[key] for key in row.keys()}
        return await self._db(query)

    async def get_operations(self, run_id, *, offset=0, limit=100):
        self.page(offset, limit)
        ids = await self._db(lambda db: [row[0] for row in db.execute(
            "SELECT id FROM operations WHERE run_id=? ORDER BY rowid LIMIT ? OFFSET ?", (run_id, limit, offset))])
        return [await self.get_operation(item) for item in ids]

    async def put_meta(self, key, value):
        await self._db(lambda db: db.execute("INSERT INTO meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                       (key, canonical(value))), write=True)

    async def record_metric(self, name, amount=1):
        if name not in {'busy', 'llm_requests', 'llm_latency_seconds', 'reconnects'} or not isinstance(amount, int | float) or not 0 <= amount <= 86400:
            raise ValueError('Metric outside fixed allowlist')
        def record(db):
            row = db.execute("SELECT value FROM meta WHERE key='metrics'").fetchone()
            values = json.loads(row[0]) if row else {}
            values[name] = values.get(name, 0) + amount
            db.execute("INSERT INTO meta VALUES('metrics',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (canonical(values),))
        await self._db(record, write=True)

    async def get_meta(self, key, default=None):
        def query(db):
            row = db.execute("SELECT value FROM meta WHERE key=?", (key,)).fetchone()
            return json.loads(row[0]) if row else default
        return await self._db(query)

    async def get_barrier(self):
        return await self._db(self._barrier)

    def _set_barrier(self, db, barrier):
        db.execute("INSERT INTO meta VALUES('safety_barrier',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                   (barrier.model_dump_json(),))

    async def set_barrier(self, barrier, *, status="uncertain"):
        def record(db):
            self._set_barrier(db, barrier)
            if barrier.run_id:
                snapshot = self._snapshot(db, barrier.run_id)
                return self._event(db, snapshot, "status", {"status": status},
                                   {"status": status, "safety_barrier": barrier})[1]
        return await self._db(record, write=True)

    async def recover(self):
        def recover(db):
            from .assets import assert_no_links
            from .contracts import JobUpdate, OperationResult
            emergency = self.directory / "emergency"
            assert_no_links(emergency)
            if emergency.exists():
                for path in emergency.glob("*.json"):
                    assert_no_links(path)
                    if not path.is_file() or path.stat().st_size > 16 * 1024**2:
                        raise AgentError("EMERGENCY_INVALID", "Emergency evidence требует ручного рассмотрения.", 503)
                    payload = json.loads(path.read_text(encoding="utf-8"))
                    evidence = OperationResult.model_validate(payload) if "outcome" in payload else JobUpdate.model_validate(payload)
                    row = db.execute("SELECT * FROM operations WHERE id=?", (evidence.operation_id,)).fetchone()
                    if (row is None or path.stem != evidence.operation_id or
                            (row["job_id"] and row["job_id"] != evidence.job_id)):
                        raise AgentError("EMERGENCY_CONFLICT", "Emergency job binding противоречит ledger; replay запрещён.", 503)
                    if row["state"] == "terminal":
                        continue
                    db.execute("UPDATE operations SET job_id=?, report=?, state=? WHERE id=?", (
                        evidence.job_id, canonical(evidence.report),
                        "terminal" if isinstance(evidence, OperationResult) else "known_job", evidence.operation_id))
                    if isinstance(evidence, OperationResult):
                        db.execute("UPDATE operations SET result=? WHERE id=?", (evidence.model_dump_json(), evidence.operation_id))
            cached = db.execute("SELECT value FROM meta WHERE key='documents'").fetchone()
            if cached:
                documents = json.loads(cached[0])
                documents["stale"] = True
                db.execute("UPDATE meta SET value=? WHERE key='documents'", (canonical(documents),))
            db.execute("UPDATE approvals SET state='invalidated' WHERE state='pending'")
            pending = []
            for row in db.execute("SELECT snapshot FROM runs WHERE unfinished=1").fetchall():
                snapshot = persisted.snapshot(json.loads(row[0]), self._workspace(db))
                operation = db.execute("SELECT * FROM operations WHERE run_id=? ORDER BY rowid DESC LIMIT 1", (snapshot.id,)).fetchone()
                changes = {"stop_requested": True}
                status = "interrupted"
                if operation and operation["job_id"] and operation["state"] != "terminal":
                    pending.append({"job_id": operation["job_id"], "operation": json.loads(operation["prepared"])})
                    status = "stopping"
                elif operation and operation["state"] == "dispatching":
                    barrier = SafetyBarrier(reason="DISPATCH_OUTCOME_UNKNOWN", run_id=snapshot.id,
                                            operation_id=operation["id"])
                    self._set_barrier(db, barrier)
                    changes["safety_barrier"], status = barrier, "uncertain"
                elif operation and operation["report"]:
                    report = json.loads(operation["report"])
                    if report.get("status") == "unknown" or (operation["job_id"] and
                            (not isinstance(report.get("cleanup"), dict) or report["cleanup"].get("completed") is not True)):
                        status = "uncertain" if report.get("status") == "unknown" else "halted"
                        barrier = SafetyBarrier(reason="RECOVERY_REVIEW_REQUIRED", run_id=snapshot.id,
                                                operation_id=operation["id"], job_id=operation["job_id"])
                        self._set_barrier(db, barrier)
                        changes["safety_barrier"] = barrier
                self._event(db, snapshot, "status", {"status": status}, {**changes, "status": status})
            return pending
        return await self._db(recover, write=True)

    async def register_asset(self, asset_id, kind, metadata):
        await self._db(lambda db: db.execute("INSERT INTO assets VALUES(?,?,?)",
                       (asset_id, kind, canonical(metadata))), write=True)

    async def get_asset(self, asset_id, kind=None):
        def query(db):
            row = db.execute("SELECT kind,metadata FROM assets WHERE id=?", (asset_id,)).fetchone()
            if not row or (kind and row[0] != kind):
                raise AgentError("ASSET_NOT_FOUND", "Зарегистрированный артефакт не найден.", 404)
            return {"kind": row[0], **json.loads(row[1])}
        return await self._db(query)

    async def list_assets(self):
        return await self._db(lambda db: [dict(row) for row in db.execute("SELECT id,kind FROM assets ORDER BY rowid LIMIT 500")])

    @staticmethod
    def _cleanup_confirmed(db, retained):
        from .cleanup import absence, commitment
        for item in db.execute("SELECT value FROM meta WHERE key LIKE ?",
                               (f"cleanup:{retained['id']}:%",)):
            try:
                audit = json.loads(item['value'])
                observed = db.execute("SELECT * FROM operations WHERE id=?",
                                      (audit['observation_id'],)).fetchone()
                if (audit['version'] == 1 and audit['operation_id'] == retained['id']
                        and audit['operation_digest'] == commitment(retained)
                        and observed is not None and audit['observation_digest'] == commitment(observed)
                        and audit['project_name'] == absence(retained, observed)):
                    return True
            except (ValueError, TypeError, KeyError):
                continue
        return False

    async def confirm_retained_cleanup(self, observation_id, owner, verify_owner):
        """Append evidence only; historical operations/reports are never rewritten."""
        from .cleanup import absence, commitment, observation
        def confirm(db):
            observed = db.execute("SELECT * FROM operations WHERE id=?", (observation_id,)).fetchone()
            try:
                observation(observed, owner_pid=owner.pid, fresh=True)
            except (ValueError, TypeError, KeyError):
                return 0
            retained = db.execute("SELECT * FROM operations WHERE "
                "json_extract(result,'$.cleanup.project_retained')=1 ORDER BY id LIMIT 100001").fetchall()
            if len(retained) > 100000 or not verify_owner():
                return 0
            count = 0
            for row in retained:
                try:
                    name = absence(row, observed)
                    if self._cleanup_confirmed(db, row):
                        continue
                    audit = {'version': 1, 'operation_id': row['id'], 'operation_digest': commitment(row),
                        'observation_id': observation_id, 'observation_digest': commitment(observed),
                        'project_name': name, 'at': utc_now(), 'owner': owner.model_dump(mode='json')}
                except (ValueError, TypeError, KeyError):
                    continue
                inserted = db.execute("INSERT OR IGNORE INTO meta(key,value) VALUES(?,?)",
                                      (f"cleanup:{row['id']}:{observation_id}", canonical(audit)))
                if inserted.rowcount != 1:
                    continue
                snapshot = self._snapshot(db, observed['run_id'])
                self._event(db, snapshot, 'warning', {'code': 'RETAINED_GMS_ABSENT',
                    'message': f'Подтверждена выгрузка {name}: проект отсутствует в GMS и VBE. '
                               f'Операция {row["id"]}; проверка {observation_id}. Старый отчёт сохранён.'})
                count += 1
            if count and not verify_owner():
                raise AgentError('READINESS_CHANGED', 'Владелец изменился во время подтверждения очистки.')
            return count
        return await self._db(confirm, write=True)

    def _readiness_proof(self, db):
        from .policy import Policy
        barrier = self._barrier(db)
        metadata = {row['key']: row['value'] for row in db.execute(
            "SELECT key,value FROM meta WHERE key IN ('documents','recovery_pending','recovery_only') OR key LIKE 'cleanup:%'")}
        unfinished = [row[0] for row in db.execute('SELECT id FROM runs WHERE unfinished=1 ORDER BY id')]
        # Project terminal evidence, not image bodies or raw reports, into the commitment.
        rows = [dict(row) for row in db.execute("""SELECT id,run_id,state,job_id,prepared,
            json_extract(result,'$.operation_id') result_id,
            json_extract(result,'$.job_id') result_job,
            json_extract(result,'$.outcome') outcome,
            json_type(result,'$.applied') applied,
            json_type(result,'$.cleanup.completed') cleaned,
            json_type(result,'$.cleanup.project_retained') retained,
            json_extract(result,'$.report.job_id') report_job,
            json_extract(result,'$.report.tool') report_tool,
            json_extract(result,'$.report.request_id') report_request,
            json_extract(result,'$.report.status') report_status
            FROM operations ORDER BY id LIMIT 100001""")]
        reason = None
        if json.loads(metadata.get('recovery_pending', '[]')) != [] or json.loads(metadata.get('recovery_only', 'false')):
            reason = 'RECOVERY_PENDING'
        elif unfinished:
            reason = 'ACTIVE_RUN'
        elif len(rows) > 100000:
            reason = 'EVIDENCE_LIMIT'
        elif barrier and ((barrier.operation_id and barrier.operation_id not in {row['id'] for row in rows})
                          or (barrier.job_id and barrier.job_id not in {row['job_id'] for row in rows})):
            reason = 'REPORT_UNCONFIRMED'
        for row in rows if reason is None else ():
            try:
                prepared = PreparedOperation.model_validate_json(row['prepared'])
                if (prepared.operation_id != row['id'] or prepared.run_id != row['run_id']
                        or prepared.policy_version not in {1, 2, 3}):
                    raise ValueError
                if row['state'] == 'prepared' and row['job_id'] is None:
                    sent = db.execute("SELECT 1 FROM events WHERE json_extract(payload,'$.type') "
                        "IN ('tool_start','job_progress','tool_result') "
                        "AND json_extract(payload,'$.data.operation_id')=? LIMIT 1", (row['id'],)).fetchone()
                    if not sent:
                        continue
                if row['state'] != 'terminal':
                    reason = 'PENDING_JOB' if row['job_id'] else 'DISPATCH_OUTCOME_UNKNOWN'
                elif row['outcome'] not in {'succeeded', 'failed'}:
                    reason = 'OUTCOME_UNKNOWN'
                elif row['cleaned'] != 'true':
                    reason = 'CLEANUP_NOT_PROVEN'
                elif row['result_id'] != row['id'] or row['result_job'] != row['job_id']:
                    reason = 'REPORT_UNCONFIRMED'
                elif row['job_id'] is None:
                    if row['outcome'] != 'failed' or row['applied'] != 'false':
                        reason = 'REPORT_UNCONFIRMED'
                elif (prepared.digest != Policy.operation_digest(prepared)
                      or row['report_job'] != row['job_id'] or row['report_status'] != row['outcome']
                      or row['report_tool'] != prepared.tool or row['report_request'] != prepared.corel_request_id):
                    reason = 'REPORT_UNCONFIRMED'
                if reason is None and row['retained'] == 'true':
                    retained = db.execute('SELECT * FROM operations WHERE id=?', (row['id'],)).fetchone()
                    if not self._cleanup_confirmed(db, retained):
                        reason = 'CLEANUP_NOT_PROVEN'
            except ValueError:
                reason = 'EVIDENCE_INVALID'
            if reason:
                break
        return {'barrier': barrier, 'reason': reason, 'documents': json.loads(metadata.get('documents', '{}')),
                'digest': fingerprint({'barrier': barrier.model_dump(mode='json') if barrier else None,
                    'metadata': metadata, 'unfinished': unfinished, 'operations': rows})}

    async def readiness_proof(self):
        return await self._db(self._readiness_proof)

    async def clear_barrier(self, barrier_id, proof_digest, verify_owner):
        def clear(db):
            proof = self._readiness_proof(db)
            if (proof['reason'] or not proof['barrier'] or proof['barrier'].id != barrier_id
                    or proof['digest'] != proof_digest or not verify_owner()):
                raise AgentError('READINESS_CHANGED', 'Состояние изменилось во время проверки readiness.')
            db.execute("DELETE FROM meta WHERE key='safety_barrier'")
        await self._db(clear, write=True)

    async def list_reviews(self, *, offset=0, limit=100):
        self.page(offset, limit)
        return await self._db(lambda db: [{'id': row['id'], 'at': row['at'], 'data': json.loads(row['payload'])}
            for row in db.execute("SELECT id,payload,at FROM reviews ORDER BY rowid DESC LIMIT ? OFFSET ?", (limit, offset))])
