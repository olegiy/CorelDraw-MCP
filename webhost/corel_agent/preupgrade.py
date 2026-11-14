"""Read-only upgrade admission. Never migrate, recover, replay or release a barrier."""

import argparse
import hashlib
import json
import sqlite3
import stat
from pathlib import Path

from .assets import assert_no_links
from .store import SCHEMA_VERSION, Store


def _fingerprint(path):
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= 2 * 1024**3:
        raise ValueError('History size/type')
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024**2), b''):
            digest.update(chunk)
    after = path.stat()
    def identity(value):
        return value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns
    if identity(info) != identity(after):
        raise ValueError('History changed')
    return identity(after), digest.hexdigest()


def inspect_history(path):
    path = Path(path).absolute()
    try:
        assert_no_links(path)
        # immutable=1 prevents SQLite from creating a WAL/SHM or changing locks in the source.
        # It must never hide an uncheckpointed transaction, hence reject both journal forms first.
        for suffix in ('-wal', '-journal'):
            journal = path.with_name(path.name + suffix)
            assert_no_links(journal)
            if journal.exists() and journal.stat().st_size:
                return {'ok': False, 'reason': 'HISTORY_NOT_CHECKPOINTED'}
        before = _fingerprint(path)
        db = sqlite3.connect(path.as_uri() + '?mode=ro&immutable=1', uri=True)
        db.row_factory = sqlite3.Row
        try:
            if (db.execute('PRAGMA user_version').fetchone()[0] != SCHEMA_VERSION
                    or db.execute('PRAGMA quick_check').fetchall()[0][0] != 'ok'
                    or db.execute('PRAGMA foreign_key_check').fetchone() is not None):
                raise ValueError('History schema/integrity')
            reader = Store(path.parent)
            runs = db.execute('SELECT id FROM runs LIMIT 100001').fetchall()
            if len(runs) > 100000:
                raise ValueError('History run limit')
            for row in runs:
                if reader._snapshot(db, row['id']).id != row['id']:
                    raise ValueError('Run identity')
            proof = reader._readiness_proof(db)
            count = db.execute('SELECT count(*) FROM operations').fetchone()[0]
            resolved = []
            if proof['reason'] is None:
                for row in db.execute("SELECT result FROM operations WHERE json_extract(result,'$.outcome')='failed'"):
                    result = json.loads(row['result'])
                    report = result.get('report') or {}
                    if (result.get('applied') is not False or (report.get('error') or {}).get('applied') is not False
                            or (report.get('result') or {}).get('partial') is True):
                        proof['reason'] = 'MUTATION_OUTCOME_UNRESOLVED'
                        break
            if proof['reason'] is None:
                keys = ('job_id', 'tool', 'request_id', 'owner_pid', 'finished_at', 'run_directory', 'cleanup', 'artifacts')
                for row in db.execute("SELECT * FROM operations WHERE json_extract(result,'$.cleanup.project_retained')=1"):
                    if reader._cleanup_confirmed(db, row):
                        report = json.loads(row['report'])
                        resolved.append({key: report[key] for key in keys if key in report})
        finally:
            db.close()
        if _fingerprint(path) != before:
            raise ValueError('History changed during admission')
        for suffix in ('-wal', '-journal'):
            journal = path.with_name(path.name + suffix)
            assert_no_links(journal)
            if journal.exists() and journal.stat().st_size:
                return {'ok': False, 'reason': 'HISTORY_NOT_CHECKPOINTED'}
        return {'ok': proof['reason'] is None, 'reason': proof['reason'],
                'operations': count, 'proof_digest': proof['digest'], 'resolved_reports': resolved}
    except Exception:
        return {'ok': False, 'reason': 'HISTORY_CHECK_INCOMPLETE'}


def main():
    parser = argparse.ArgumentParser(description='Проверить историю перед обновлением без её изменения.')
    parser.add_argument('--history', required=True)
    result = inspect_history(parser.parse_args().history)
    print(json.dumps(result, ensure_ascii=False, allow_nan=False))
    return 0 if result['ok'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
