import json

from conftest import implementation


def test_technical_log_is_allowlisted_and_rotated_without_user_content(tmp_path):
    module = implementation("observability")
    log = module.TechnicalLog(tmp_path, max_bytes=300, backups=2)
    for _ in range(20):
        log.write("operation", code="CLEANUP_FAILED", outcome="halted", duration_seconds=1.5,
                  prompt="CANARY prompt", code_text="CANARY VBA", path="C:/private.cdr",
                  authorization="CANARY key", detail={"secret": "CANARY"})
    files = list(tmp_path.glob("*.jsonl*"))
    assert 1 <= len(files) <= 3
    for path in files:
        text = path.read_text(encoding="utf-8")
        assert "CANARY" not in text and "private.cdr" not in text
        for line in text.splitlines():
            record = json.loads(line)
            assert record["code"] == "CLEANUP_FAILED"
            assert "at" in record and "prompt" not in record


async def test_metrics_and_diagnostics_exclude_report_code_and_paths(store):
    module = implementation("observability")
    report = await module.diagnostics(store, mcp_connected=True, slow_subscribers=2)
    assert report["schema_version"] == 1
    assert "storage" in report and report["mcp_connected"] is True
    assert str(store.directory) not in json.dumps(report)
    assert "events" not in report and "messages" not in report


async def test_runtime_lifecycle_log_is_allowlisted_and_excludes_report_data(runtime, store, tmp_path):
    module = implementation('observability')
    log = module.TechnicalLog(tmp_path)
    runtime.technical_log = log
    from conftest import command, wait_run
    run = await runtime.start(command(message='CANARY prompt C:/private.cdr'))
    await wait_run(store, run.id)
    log.close()
    text = (tmp_path / 'technical.jsonl').read_text(encoding='utf-8')
    assert 'CANARY' not in text and 'private.cdr' not in text
    assert 'run_id' in text and 'outcome' in text


async def test_metrics_count_unsafe_jobs_without_payload_labels(store):
    from conftest import command

    from corel_agent.contracts import JobUpdate, OperationResult, PreparedOperation, new_id
    run = (await store.accept_run(command(), None)).snapshot
    operation = PreparedOperation(operation_id=new_id(), run_id=run.id, tool='corel_status',
                                  arguments={}, digest='a' * 64)
    await store.record_operation(operation)
    await store.mark_dispatching(operation.operation_id)
    import asyncio
    await asyncio.sleep(0.03)
    for _ in range(2):
        await store.record_job(JobUpdate(operation_id=operation.operation_id, job_id='fixture-job',
            status='timed_out', stage='waiting', monitoring_state='degraded',
            report={'status': 'timed_out', 'result': {'private': 'CANARY-document'}}))
    await store.complete_operation(OperationResult(operation_id=operation.operation_id, job_id='fixture-job',
        outcome='unknown', applied=None, report={'status': 'unknown'}, cleanup={'completed': False}))
    summary = await implementation('observability').diagnostics(store, mcp_connected=False)
    metrics = summary['metrics']
    assert metrics['timed_out'] == 1 and metrics['unknown'] == 1
    assert metrics['cleanup_failures'] == 1 and metrics['monitoring_degraded'] == 1
    assert metrics['corel_latency_seconds'] >= 0.02
    assert 'CANARY' not in json.dumps(metrics) and 'fixture-job' not in json.dumps(metrics)


async def test_agent_latency_and_busy_are_fixed_aggregate_metrics(runtime, store):
    import asyncio

    import pytest
    from conftest import wait_run
    from test_runtime import completion, configure

    from corel_agent.contracts import AgentError, new_id
    provider, command = await configure(runtime, store, [completion()])
    provider.release = asyncio.Event()
    run = await runtime.start(command)
    await provider.started.wait()
    with pytest.raises(AgentError) as caught:
        await runtime.start(command.model_copy(update={'client_request_id': new_id()}))
    assert caught.value.code == 'BUSY'
    provider.release.set()
    await wait_run(store, run.id)
    metrics = (await implementation('observability').diagnostics(store, mcp_connected=True))['metrics']
    assert metrics['busy'] == 1 and metrics['llm_requests'] == 1
    assert metrics['llm_latency_seconds'] > 0
