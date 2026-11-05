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
