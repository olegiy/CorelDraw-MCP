"""Observe durable native reports only after old process exit is independently verified."""

import asyncio
import json
from pathlib import Path

from .assets import assert_no_links
from .contracts import AgentError, OperationResult, PreparedOperation, ProcessIdentity
from .corel_mcp import JOB_ID
from .owned_stdio import identity_alive, release_marker


async def reconcile(store, bindings, directory, reports):
    marker = Path(directory) / 'mcp-owner.json'
    def read():
        assert_no_links(marker)
        original = marker.read_bytes()
        data = json.loads(original)
        if data.get('phase') != 'running':
            raise AgentError('OWNERSHIP_UNCERTAIN', 'Spawn boundary прежнего MCP не подтверждена.', 503)
        identity = ProcessIdentity.model_validate(data['process'])
        if identity_alive(identity) is not False:
            raise AgentError('OWNERSHIP_UNCERTAIN', 'Прежний MCP жив или его завершение не доказано.', 503)
        results = []
        for binding in bindings:
            job_id = binding['job_id']
            if not JOB_ID.fullmatch(job_id):
                raise AgentError('JOB_BINDING_INVALID', 'Неверная сохранённая привязка Job.', 503)
            operation = PreparedOperation.model_validate(binding['operation'])
            path = Path(reports) / job_id / 'report.json'
            assert_no_links(path)
            if not path.is_file() or not 0 < path.stat().st_size <= 16 * 1024**2:
                raise AgentError('REPORT_UNAVAILABLE', 'Оригинальный Corel-отчёт недоступен.', 503)
            report = json.loads(path.read_text(encoding='utf-8'))
            if (report.get('job_id') != job_id or report.get('tool') != operation.tool
                    or report.get('request_id') != operation.corel_request_id
                    or report.get('owner_pid') != identity.pid
                    or report.get('status') not in {'succeeded', 'failed', 'unknown'}):
                raise AgentError('REPORT_UNCONFIRMED', 'Terminal report не соответствует прежней операции.', 503)
            value = report.get('error', {}).get('applied')
            if not isinstance(value, bool):
                value = report.get('result', {}).get('applied')
            results.append(OperationResult(operation_id=operation.operation_id, job_id=job_id,
                outcome=report['status'], report=report, cleanup=report.get('cleanup'),
                applied=value if isinstance(value, bool) else None))
        return original, identity, results
    original, identity, results = await asyncio.to_thread(read)
    for result in results:
        await store.complete_operation(result)
        operation = await store.get_operation(result.operation_id)
        snapshot = await store.get_snapshot(operation['run_id'])
        if snapshot.safety_barrier is None:
            await store.set_status(snapshot.id, 'interrupted', stop_requested=True)
    def release():
        assert_no_links(marker)
        if marker.read_bytes() != original or identity_alive(identity) is not False:
            raise AgentError('OWNERSHIP_UNCERTAIN', 'Владелец изменился в ходе reconciliation.', 503)
        release_marker(marker, original)
    await asyncio.to_thread(release)
    await store.put_meta('recovery_pending', [])
    return True
