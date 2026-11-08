import pytest
from conftest import implementation

from corel_agent.contracts import AgentError


async def test_export_artifact_is_verified_without_root_membership(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    path = tmp_path / "export.svg"
    path.write_text('<svg xmlns="http://www.w3.org/2000/svg"/>')
    artifact = await assets.register_export(str(path), "svg")
    data, metadata = await assets.read_artifact(artifact.artifact_id)
    assert b"<svg" in data and metadata["media_type"] == "image/svg+xml"
    path.write_text("changed")
    with pytest.raises(AgentError):
        await assets.read_artifact(artifact.artifact_id)


async def test_report_path_does_not_grant_file_download(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    output = tmp_path / "allowed"
    output.mkdir()
    outside = tmp_path / "private.svg"
    outside.write_text("private")
    with pytest.raises(AgentError):
        await assets.read_artifact(str(outside))
    artifact = await assets.register_export(str(outside), 'svg')
    assert (await assets.read_artifact(artifact.artifact_id))[0] == b'private'


async def test_diagnostic_artifact_contains_only_allowlisted_data(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    report = await implementation("observability").diagnostics(store, mcp_connected=True)
    artifact = await assets.save_diagnostics(report)
    data, metadata = await assets.read_artifact(artifact.artifact_id)
    assert metadata["media_type"] == "application/json" and b'"runs"' in data
    assert "path" not in artifact.model_dump()


async def test_runtime_registers_only_actual_scoped_export_result(runtime, store, tmp_path):
    from conftest import command, wait_run
    from test_approvals import prime
    await prime(runtime, store)
    await store.put_meta("workspace", {"output_directory": str(tmp_path)})
    destination = tmp_path / 'result.svg'
    destination.write_bytes(b'original sentinel')
    run = await runtime.start(command(tool="corel_export",
        document_id="fixture-b", arguments={"path": "result.svg", "format": "svg"}))
    assert (await wait_run(store, run.id)).status == "completed"
    registered = await store.list_assets()
    assert len(registered) == 1 and registered[0]["kind"] == "export"
    data, _ = await runtime.assets.read_artifact(registered[0]["id"])
    assert b"<svg" in data
    assert destination.read_bytes() == data
    assert not any(event.type == 'confirmation' for event in await store.get_events(run.id))


async def test_manual_save_without_path_uses_exact_current_document(runtime, store, tmp_path):
    from conftest import command, wait_run
    from test_approvals import calls, prime
    await prime(runtime, store)
    destination = tmp_path / 'current.cdr'
    first = await runtime.start(command(tool='corel_save_document',
        document_id='fixture-b', arguments={'path': str(destination)}))
    assert (await wait_run(store, first.id)).status == 'completed'
    second = await runtime.start(command(tool='corel_save_document', document_id='fixture-b'))
    assert (await wait_run(store, second.id)).status == 'completed'
    assert calls(store, 'corel_save_document')[-1]['args']['path'] == str(destination)


async def test_internal_assets_directory_is_not_a_user_export_target(runtime, store):
    from conftest import command
    from test_approvals import calls, prime
    await prime(runtime, store)
    (store.directory / 'assets').mkdir()
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_export', document_id='fixture-b',
                                    arguments={'path': str(store.directory / 'assets/result.svg'), 'format': 'svg'}))
    assert calls(store, 'corel_export') == []
