"""Owned technical output: fixed fields, bounded labels, never prompts/code/paths."""

import json
import logging
import re
from logging.handlers import RotatingFileHandler
from threading import RLock

from .assets import assert_no_links
from .contracts import utc_now

ALLOWED = frozenset({"trace_id", "run_id", "operation_id", "job_id", "stage", "code", "duration_seconds", "outcome"})
TOKEN = re.compile(r"^[a-zA-Z0-9_:.-]{1,160}$")


class TechnicalLog:
    def __init__(self, directory, *, max_bytes=10 * 1024**2, backups=5):
        assert_no_links(directory)
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / "technical.jsonl"
        assert_no_links(path)
        self.handler = RotatingFileHandler(path, maxBytes=max_bytes, backupCount=backups,
                                           encoding="utf-8", delay=True)
        self._lock = RLock()

    def write(self, event, **values):
        record = {"at": utc_now(), "level": "INFO", "event": event if TOKEN.fullmatch(event) else "invalid"}
        for key in ALLOWED:
            value = values.get(key)
            if isinstance(value, str) and TOKEN.fullmatch(value):
                record[key] = value
            elif key == "duration_seconds" and type(value) in {int, float} and 0 <= value <= 86400:
                record[key] = value
        with self._lock:
            self.handler.emit(logging.LogRecord("coreldraw_agent", logging.INFO, "", 0,
                json.dumps(record, ensure_ascii=False, allow_nan=False), (), None))

    def close(self):
        with self._lock:
            self.handler.close()


async def diagnostics(store, *, mcp_connected, slow_subscribers=0):
    def counts(db):
        outcomes = {row[0]: row[1] for row in db.execute(
            "SELECT json_extract(snapshot,'$.status'),count(*) FROM runs GROUP BY 1")}
        totals = db.execute("SELECT count(*),coalesce(sum(json_extract(snapshot,'$.counters.turns')),0) FROM runs").fetchone()
        operations = db.execute("SELECT count(*) FROM operations").fetchone()[0]
        approvals = db.execute("SELECT count(*) FROM approvals").fetchone()[0]
        timed_out = db.execute("SELECT count(DISTINCT json_extract(payload,'$.data.operation_id')) FROM events WHERE json_extract(payload,'$.type')='job_progress' AND json_extract(payload,'$.data.status')='timed_out'").fetchone()[0]
        degraded = db.execute("SELECT count(DISTINCT json_extract(payload,'$.data.operation_id')) FROM events WHERE json_extract(payload,'$.type')='job_progress' AND json_extract(payload,'$.state.monitoring_state')='degraded'").fetchone()[0]
        unsafe = db.execute("SELECT coalesce(sum(json_extract(result,'$.outcome')='unknown'),0), coalesce(sum(json_extract(result,'$.cleanup.completed')=0),0) FROM operations").fetchone()
        latency = db.execute("SELECT coalesce(sum(max(0,(julianday(t.finished)-julianday(t.started))*86400)),0) FROM (SELECT json_extract(payload,'$.data.operation_id') AS id, min(CASE WHEN json_extract(payload,'$.type')='tool_start' THEN json_extract(payload,'$.at') END) AS started, max(CASE WHEN json_extract(payload,'$.type')='tool_result' THEN json_extract(payload,'$.at') END) AS finished FROM events GROUP BY id) AS t WHERE t.id IS NOT NULL").fetchone()[0]
        return {"runs": totals[0], "llm_turns": totals[1], "corel_operations": operations,
                "approvals": approvals, "outcomes": outcomes, "timed_out": timed_out,
                "unknown": unsafe[0], "cleanup_failures": unsafe[1],
                "monitoring_degraded": degraded, "corel_latency_seconds": latency}
    metrics = await store._db(counts)
    extras = await store.get_meta('metrics', {})
    metrics.update({name: extras.get(name, 0) for name in ('busy', 'llm_requests', 'llm_latency_seconds', 'reconnects')})
    return {"schema_version": 1, "at": utc_now(), "mcp_connected": bool(mcp_connected),
            "store_ready": not store.failed, "storage": await store.storage_state(),
            "metrics": {**metrics, "slow_subscribers": slow_subscribers},
            "safety_barrier_present": await store.get_barrier() is not None}
