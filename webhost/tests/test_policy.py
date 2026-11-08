
import pytest
from conftest import implementation

from corel_agent.contracts import AgentError, OperationContext, new_id


@pytest.fixture
def context(tmp_path):
    return OperationContext(run_id=new_id(), document_id="fixture-b")


@pytest.fixture
def policy(runtime):
    return implementation("policy").Policy(runtime.mcp.catalog)


async def test_vba_is_allowed_without_confirmation(policy, context):
    code = 'Function Main() As String\n Main = "{\"\"ok\"\":true}"\nEnd Function'
    operation = await policy.prepare("corel_execute_vba", {"code": code}, context)
    decision = await policy.classify(operation, context)
    assert decision.decision == "allow"
    assert operation.arguments["code"] == code
    assert operation.corel_request_id == f"web:{operation.operation_id}"
    assert operation.arguments["document_id"] == context.document_id
    assert operation.arguments["deadline_seconds"] == 600
    assert operation.required_postchecks == ("corel_inspect_document", "corel_preview")


async def test_export_replaces_destination_without_content_approval(policy, context, tmp_path):
    path = str(tmp_path / "рисунок.png")
    operation = await policy.prepare("corel_export", {"path": path, "format": "png"}, context)
    assert (await policy.classify(operation, context)).decision == "allow"
    assert operation.arguments["path"] == str(tmp_path / "рисунок.png")
    (tmp_path / "рисунок.png").write_bytes(b"original sentinel")
    operation = await policy.prepare("corel_export", {"path": path, "format": "png"}, context)
    assert (await policy.classify(operation, context)).decision == "allow"
    (tmp_path / "рисунок.png").write_bytes(b"changed sentinel")
    assert (await policy.classify(operation, context)).decision == "allow"


@pytest.mark.parametrize("tool,args", [
    ("corel_execute_vba", {"code": "fixture"}), ("corel_create_document", {}),
])
async def test_mutation_classification_uses_binding_not_read_only_permission(policy, context, tool, args):
    context = context.model_copy(update={"new_document": True,
                                         "document_id": None if tool == "corel_create_document" else context.document_id})
    operation = await policy.prepare(tool, args, context)
    assert (await policy.classify(operation, context)).decision == "allow"


@pytest.mark.parametrize("field,value", [
    ("document_id", "fixture-a"), ("request_id", "attacker"), ("deadline_seconds", 3600),
    ("base_url", "https://attacker.example"),
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
    "@output/bad\x00.png", "relative.png", "C:\\bad\x00.png",
])
async def test_unsafe_output_paths_are_denied(policy, context, path):
    with pytest.raises(AgentError):
        await policy.prepare("corel_export", {"path": path, "format": "png"}, context)


async def test_unregistered_unicode_cdr_is_opened_without_code_rewriting(policy, context, tmp_path):
    path = tmp_path / "Чертёж с пробелом' №1.cdr"
    path.write_bytes(b"local cdr fixture")
    context = context.model_copy(update={"document_id": None, "new_document": True})
    operation = await policy.prepare("corel_open_document", {"path": str(path)}, context)
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


async def test_cached_current_save_path_is_used_without_overwrite_flag(policy, context, tmp_path):
    path = tmp_path / 'previous-current.cdr'
    path.write_bytes(b'preserve original')
    context = context.model_copy(update={'current_document_path': str(path)})
    operation = await policy.prepare('corel_save_document', {}, context)
    assert (await policy.classify(operation, context)).decision == 'allow'
    assert path.read_bytes() == b'preserve original'


async def test_missing_tool_and_invalid_format_fail_closed(policy, context):
    with pytest.raises(AgentError):
        await policy.prepare("corel_prune_artifacts", {}, context)
    with pytest.raises(AgentError):
        await policy.prepare("corel_export", {"path": "@output/export.jpg", "format": "jpg"}, context)


def test_user_output_directory_may_be_junction(tmp_path):
    import subprocess
    guard = implementation("policy").PathGuard()
    target, link = tmp_path / "target", tmp_path / "junction"
    target.mkdir()
    result = subprocess.run(["cmd.exe", "/c", "mklink", "/J", str(link), str(target)],
                            capture_output=True, check=True)
    assert result.returncode == 0
    try:
        assert guard.directory(str(link)) == target
    finally:
        link.rmdir()


async def test_export_absolute_path_is_not_limited_by_workspace(policy, context, tmp_path):
    outside = tmp_path / 'other'
    outside.mkdir()
    operation = await policy.prepare('corel_export', {'path': str(outside / 'result.png'), 'format': 'png'},
                                     context.model_copy(update={'output_directory': None}))
    assert operation.arguments['path'] == str(outside / 'result.png')
    assert (await policy.classify(operation, context.model_copy(update={'output_directory': None}))).decision == 'allow'


@pytest.mark.parametrize('tool,format', [('corel_save_document', 'cdr'), ('corel_export', 'png')])
async def test_removed_overwrite_argument_is_rejected(policy, context, tmp_path, tool, format):
    with pytest.raises(AgentError) as caught:
        await policy.prepare(tool, {'path': str(tmp_path / f'result.{format}'),
                                   **({'format': format} if tool == 'corel_export' else {}), 'overwrite': True}, context)
    assert caught.value.status == 422


async def test_new_operations_are_version_two_and_version_one_is_evidence_only(policy, context):
    operation = await policy.prepare('corel_execute_vba', {'code': 'fixture'}, context)
    assert operation.policy_version == 2
    old = operation.model_copy(update={'policy_version': 1})
    old = old.model_copy(update={'digest': policy.operation_digest(old)})
    assert (await policy.classify(old, context)).decision == 'deny'


def test_catalog_exposes_close_and_export_without_permission_context(policy, context):
    names = {tool.name for tool in policy.catalog(context)}
    assert {'corel_close_document', 'corel_export', 'corel_execute_vba', 'corel_save_document'} <= names


@pytest.mark.parametrize('binding', ['current_document_path', 'document_id'])
async def test_known_credential_is_rejected_after_host_binding(policy, context, tmp_path, binding):
    secret = 'fixture-bound-key'
    policy.privacy.register_secret(secret)
    if binding == 'current_document_path':
        path = tmp_path / (secret + '.cdr')
        path.write_bytes(b'local fixture')
        context = context.model_copy(update={binding: str(path)})
        tool = 'corel_save_document'
    else:
        context = context.model_copy(update={binding: secret})
        tool = 'corel_inspect_document'
    with pytest.raises(AgentError) as caught:
        await policy.prepare(tool, {}, context)
    assert caught.value.code == 'SECRET_IN_OPERATION'


async def test_credential_registered_after_prepare_is_rechecked_for_any_tool(policy, context, tmp_path):
    secret = 'fixture-late-key'
    operation = await policy.prepare('corel_export', {'path': str(tmp_path / (secret + '.png')), 'format': 'png'}, context)
    policy.privacy.register_secret(secret)
    assert (await policy.classify(operation, context)).decision == 'deny'


def test_removed_permission_config_cannot_be_forwarded_as_hidden_child_input(tmp_path):
    with pytest.raises(AgentError) as caught:
        implementation('owned_stdio').restricted_environment({'CORELDRAW_MCP_CONFIG_ROOT': str(tmp_path)})
    assert caught.value.code == 'CHILD_ENV_DENIED'
