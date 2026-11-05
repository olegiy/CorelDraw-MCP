
import pytest
from conftest import implementation

from corel_agent.contracts import AgentError, OperationContext, OutputRoot, new_id


@pytest.fixture
def context(tmp_path):
    return OperationContext(run_id=new_id(), document_id="fixture-b", read_only=False,
                            output_root=OutputRoot(path=str(tmp_path)))


@pytest.fixture
def policy(runtime):
    return implementation("policy").Policy(runtime.mcp.catalog)


async def test_vba_always_requires_confirmation(policy, context):
    code = 'Function Main() As String\n Main = "{\"\"ok\"\":true}"\nEnd Function'
    operation = await policy.prepare("corel_execute_vba", {"code": code}, context)
    decision = await policy.classify(operation, context)
    assert decision.decision == "confirm" and "VBA" in " ".join(decision.reasons)
    assert operation.arguments["code"] == code
    assert operation.corel_request_id == f"web:{operation.operation_id}"
    assert operation.arguments["document_id"] == context.document_id
    assert operation.arguments["deadline_seconds"] == 600
    assert operation.required_postchecks == ("corel_inspect_document", "corel_preview")


async def test_new_export_allowed_overwrite_confirmed(policy, context, tmp_path):
    operation = await policy.prepare("corel_export", {"path": "@output/рисунок.png", "format": "png"}, context)
    assert (await policy.classify(operation, context)).decision == "allow"
    assert operation.arguments["path"] == str(tmp_path / "рисунок.png")
    (tmp_path / "рисунок.png").write_bytes(b"original sentinel")
    with pytest.raises(AgentError):
        await policy.prepare("corel_export", {"path": "@output/рисунок.png", "format": "png"}, context)
    operation = await policy.prepare("corel_export", {"path": "@output/рисунок.png", "format": "png", "overwrite": True}, context)
    assert (await policy.classify(operation, context)).decision == "confirm"
    (tmp_path / "рисунок.png").write_bytes(b"changed sentinel")
    assert (await policy.classify(operation, context)).decision == "deny"


@pytest.mark.parametrize("tool,args", [
    ("corel_execute_vba", {"code": "fixture"}), ("corel_create_document", {}),
    ("corel_export", {"path": "@output/readonly.png", "format": "png"}),
])
async def test_read_only_mode_denies_mutations(policy, context, tool, args):
    context = context.model_copy(update={"read_only": True, "allow_new_document": True,
                                         "document_id": None if tool == "corel_create_document" else context.document_id})
    operation = await policy.prepare(tool, args, context)
    assert (await policy.classify(operation, context)).decision == "deny"


@pytest.mark.parametrize("field,value", [
    ("document_id", "fixture-a"), ("request_id", "attacker"), ("deadline_seconds", 3600),
    ("read_only", False), ("base_url", "https://attacker.example"),
])
async def test_model_cannot_override_binding(policy, context, field, value):
    with pytest.raises(AgentError) as caught:
        await policy.prepare("corel_inspect_document", {field: value}, context)
    assert caught.value.code == "BINDING_OVERRIDE"


async def test_changed_code_or_document_digest_is_denied(policy, context):
    operation = await policy.prepare("corel_execute_vba", {"code": "first code"}, context)
    operation.arguments["code"] = "changed code"
    assert (await policy.classify(operation, context)).decision == "deny"
    operation = await policy.prepare("corel_execute_vba", {"code": "first code"}, context)
    assert (await policy.classify(operation, context.model_copy(update={"document_id": "fixture-a"}))).decision == "deny"


async def test_model_catalog_is_mvp_intersection_and_has_no_system_fields(policy, context):
    tools = policy.catalog(context)
    names = {tool.name for tool in tools}
    assert {"corel_execute_vba", "corel_preview", "corel_export"} <= names
    assert not names & {"corel_get_job", "corel_prune_artifacts", "corel_build_vba", "corel_add_note"}
    for tool in tools:
        assert not {"document_id", "request_id", "deadline_seconds"} & tool.input_schema["properties"].keys()
    exported = next(tool for tool in tools if tool.name == "corel_export")
    assert exported.input_schema["properties"]["format"]["enum"] == ["png", "svg", "pdf"]


@pytest.mark.parametrize("path", [
    "@output/../escape.png", "@output/sub/file.png", "@output/CON.png",
    "@output/COM1.png", "@output/ads.png:secret", "@output/bad.png.",
    "@output/bad\x00.png", "C:\\unregistered.png", "\\\\server\\share\\file.png",
])
async def test_unsafe_output_paths_are_denied(policy, context, path):
    with pytest.raises(AgentError):
        await policy.prepare("corel_export", {"path": path, "format": "png"}, context)


async def test_registered_unicode_cdr_is_resolved_without_code_rewriting(policy, context, tmp_path):
    path = tmp_path / "Чертёж с пробелом' №1.cdr"
    path.write_bytes(b"local cdr fixture")
    guard = implementation("policy").PathGuard()
    reference = await guard.register_file(str(path))
    context = context.model_copy(update={"document_id": None, "allow_new_document": True, "files": (reference,)})
    operation = await policy.prepare("corel_open_document", {"path": reference.file_ref}, context)
    assert operation.arguments["path"] == str(path)
    assert (await policy.classify(operation, context)).decision == "allow"
    path.write_bytes(b"different bytes")
    assert (await policy.classify(operation, context)).decision == "deny"


async def test_save_selected_cdr_current_path_is_ordinary(policy, context, tmp_path):
    path = tmp_path / "selected.cdr"
    path.write_bytes(b"selected cdr")
    context = context.model_copy(update={"current_document_path": str(path)})
    operation = await policy.prepare("corel_save_document", {}, context)
    assert operation.arguments["path"] == str(path)
    assert (await policy.classify(operation, context)).decision == "allow"


async def test_cached_current_path_never_grants_unapproved_overwrite(policy, context, tmp_path):
    path = tmp_path / 'previous-current.cdr'
    path.write_bytes(b'preserve original')
    context = context.model_copy(update={'current_document_path': str(path)})
    operation = await policy.prepare('corel_save_document', {'overwrite': True}, context)
    assert (await policy.classify(operation, context)).decision == 'confirm'
    assert path.read_bytes() == b'preserve original'


async def test_missing_tool_and_invalid_format_fail_closed(policy, context):
    with pytest.raises(AgentError):
        await policy.prepare("corel_prune_artifacts", {}, context)
    with pytest.raises(AgentError):
        await policy.prepare("corel_export", {"path": "@output/export.jpg", "format": "jpg"}, context)


def test_operator_path_guard_rejects_junction(tmp_path):
    import subprocess
    guard = implementation("policy").PathGuard()
    target, link = tmp_path / "target", tmp_path / "junction"
    target.mkdir()
    result = subprocess.run(["cmd.exe", "/c", "mklink", "/J", str(link), str(target)],
                            capture_output=True, check=True)
    assert result.returncode == 0
    with pytest.raises(AgentError):
        guard.directory(str(link))
    link.rmdir()
