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

from .contracts import (
    EVENT_ADAPTER,
    TERMINAL_STATUSES,
    AcceptedRun,
    AgentError,
    EventState,
    RunSnapshot,
    SafetyBarrier,
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
                except (sqlite3.Error, OSError, ValidationError, json.JSONDecodeError):
                    self.failed = True
                    raise AgentError("STORE_UNAVAILABLE", "Не удалось прочитать или сохранить durable state.", 503) from None
        return await asyncio.to_thread(execute)

    def _snapshot(self, db, run_id):
        row = db.execute("SELECT snapshot FROM runs WHERE id=?", (run_id,)).fetchone()
        if row is None:
            raise AgentError("RUN_NOT_FOUND", "Запуск не найден.", 404, retryable=True)
        return RunSnapshot.model_validate_json(row[0])

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

    async def accept_run(self, command, profile_snapshot):
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
                if not command.read_only and self._barrier(db):
                    raise AgentError("SAFETY_BARRIER", "Неизвестный исход требует операторского рассмотрения.", 503)
                if not command.read_only and self._storage()["full"]:
                    raise AgentError("STORAGE_FULL", "Лимит хранилища исчерпан; новый изменяющий запуск запрещён.", 503)
            snapshot = RunSnapshot(id=new_id(), client_request_id=command.client_request_id,
                conversation_id=command.conversation_id, kind=command.kind,
                profile=profile_snapshot, document_id=command.document_id, read_only=command.read_only,
                output_root_id=command.output_root_id, max_calls=command.max_calls,
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
        from .contracts import RunCommand
        return await self._db(lambda db: RunCommand.model_validate_json(
            db.execute("SELECT command FROM runs WHERE id=?", (run_id,)).fetchone()[0]))

    @staticmethod
    def page(offset, limit):
        if not isinstance(offset, int) or not isinstance(limit, int) or offset < 0 or not 1 <= limit <= 500:
            raise AgentError("PAGE_INVALID", "Неверные границы страницы.", 422)

    async def list_runs(self, *, offset=0, limit=100, conversation_id=None):
        self.page(offset, limit)
        def query(db):
            rows = db.execute("SELECT snapshot FROM runs WHERE (? IS NULL OR conversation_id=?) ORDER BY rowid DESC LIMIT ? OFFSET ?",
                              (conversation_id, conversation_id, limit, offset)).fetchall()
            return [RunSnapshot.model_validate_json(row[0]) for row in rows]
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
                "stop_requested": True, "status": status, "pending_confirmation": None,
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
            return [EVENT_ADAPTER.validate_json(row[0]) for row in rows]
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

    async def mark_dispatching(self, operation_id):
        def dispatch(db):
            row = db.execute("SELECT * FROM operations WHERE id=?", (operation_id,)).fetchone()
            snapshot = self._snapshot(db, row["run_id"])
            if row["state"] != "prepared" or snapshot.stop_requested or snapshot.pending_confirmation:
                raise AgentError("DISPATCH_BLOCKED", "Операция не разрешена для отправки.")
            if snapshot.counters.calls >= snapshot.max_calls:
                raise AgentError("CALL_LIMIT", "Исчерпан бюджет вызовов.")
            db.execute("UPDATE operations SET state='dispatching' WHERE id=?", (operation_id,))
            prepared = json.loads(row["prepared"])
            counters = snapshot.counters.model_copy(update={"calls": snapshot.counters.calls + 1})
            return self._event(db, snapshot, "tool_start", {
                "operation_id": operation_id, "tool": prepared["tool"],
            }, {"counters": counters})[0]
        return await self._db(dispatch, write=True)

    async def await_confirmation(self, run_id, pending):
        def record(db):
            snapshot = self._snapshot(db, run_id)
            row = db.execute("SELECT prepared,state FROM operations WHERE id=? AND run_id=?",
                             (pending.operation.operation_id, run_id)).fetchone()
            if (snapshot.stop_requested or snapshot.status in TERMINAL_STATUSES
                    or snapshot.pending_confirmation or not row or row["state"] != "prepared"
                    or json.loads(row["prepared"]) != pending.operation.model_dump(mode="json")):
                raise AgentError("APPROVAL_STALE", "Операция изменилась или остановлена.")
            db.execute("INSERT INTO approvals VALUES(?,?,?,'pending')",
                       (pending.confirmation_id, run_id, pending.operation_digest))
            return self._event(db, snapshot, "confirmation", pending, {
                "status": "awaiting_confirmation", "pending_confirmation": pending,
            })[0]
        return await self._db(record, write=True)

    async def confirm(self, run_id, command):
        def confirm(db):
            snapshot = self._snapshot(db, run_id)
            pending = snapshot.pending_confirmation
            if (snapshot.stop_requested or snapshot.status != "awaiting_confirmation" or not pending
                    or pending.confirmation_id != command.confirmation_id
                    or pending.operation_digest != command.operation_digest):
                raise AgentError("APPROVAL_STALE", "Подтверждение устарело, изменено или уже использовано.")
            changed = db.execute("UPDATE approvals SET state=? WHERE id=? AND run_id=? AND digest=? AND state='pending'", (
                "approved" if command.approved else "rejected", command.confirmation_id, run_id,
                command.operation_digest,
            )).rowcount
            if changed != 1:
                raise AgentError("APPROVAL_STALE", "Подтверждение уже использовано.")
            status = "running" if command.approved else "rejected"
            return self._event(db, snapshot, "status", {"status": status}, {
                "status": status, "pending_confirmation": None,
            })[1]
        return await self._db(confirm, write=True)

    async def invalidate_confirmation(self, run_id, reason):
        def invalidate(db):
            db.execute("UPDATE approvals SET state='invalidated' WHERE run_id=? AND state='pending'", (run_id,))
            snapshot = self._snapshot(db, run_id)
            return self._event(db, snapshot, "error", {
                "code": "APPROVAL_STALE", "message": reason,
            }, {"status": "rejected", "pending_confirmation": None})[1]
        return await self._db(invalidate, write=True)

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
            return self._event(db, snapshot, "tool_result", {
                "operation_id": result.operation_id, "outcome": result.outcome, "applied": result.applied,
                "cleanup": result.cleanup, "error": result.report.get("error"),
            }, {"active_operation_id": None})[0]
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
                                   {"status": status, "safety_barrier": barrier, "pending_confirmation": None})[1]
        return await self._db(record, write=True)

    async def recover(self):
        def recover(db):
            db.execute("UPDATE approvals SET state='invalidated' WHERE state='pending'")
            pending = []
            for row in db.execute("SELECT snapshot FROM runs WHERE unfinished=1").fetchall():
                snapshot = RunSnapshot.model_validate_json(row[0])
                operation = db.execute("SELECT * FROM operations WHERE run_id=? ORDER BY rowid DESC LIMIT 1", (snapshot.id,)).fetchone()
                changes = {"stop_requested": True, "pending_confirmation": None}
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
                    if report.get("status") == "unknown" or report.get("cleanup", {}).get("completed") is False:
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
