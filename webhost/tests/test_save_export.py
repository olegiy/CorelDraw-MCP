import pytest
from conftest import implementation

from corel_agent.contracts import AgentError, OutputRoot


async def test_export_artifact_is_registered_only_inside_operator_root(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    root = OutputRoot(path=str(tmp_path))
    path = tmp_path / "export.svg"
    path.write_text('<svg xmlns="http://www.w3.org/2000/svg"/>')
    artifact = await assets.register_export(str(path), root, "svg")
    data, metadata = await assets.read_artifact(artifact.artifact_id)
    assert b"<svg" in data and metadata["media_type"] == "image/svg+xml"
    path.write_text("changed")
    with pytest.raises(AgentError):
        await assets.read_artifact(artifact.artifact_id)


async def test_report_path_does_not_grant_file_download(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    output = tmp_path / "allowed"
    output.mkdir()
    root = OutputRoot(path=str(output))
    outside = tmp_path / "private.svg"
    outside.write_text("private")
    with pytest.raises(AgentError):
        await assets.register_export(str(outside), root, "svg")
    with pytest.raises(AgentError):
        await assets.read_artifact(str(outside))


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
    root = OutputRoot(path=str(tmp_path))
    await store.put_meta("workspace", {"output_roots": [root.model_dump()]})
    run = await runtime.start(command(tool="corel_export", read_only=False,
        document_id="fixture-b", output_root_id=root.id, arguments={"path": "@output/result.svg", "format": "svg"}))
    assert (await wait_run(store, run.id)).status == "completed"
    registered = await store.list_assets()
    assert len(registered) == 1 and registered[0]["kind"] == "export"
    data, _ = await runtime.assets.read_artifact(registered[0]["id"])
    assert b"<svg" in data
