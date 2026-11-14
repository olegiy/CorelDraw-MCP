import hashlib
import json
import shutil
import subprocess

import pytest
from conftest import ROOT
from jsonschema import Draft202012Validator

from corel_agent.contracts import AgentError, OperationContext, ToolDefinition, new_id
from corel_agent.policy import Policy


@pytest.fixture(scope='session')
def typed_catalog():
    result = subprocess.run([shutil.which('node'), str(ROOT / 'webhost/tests/fixtures/catalog.mjs')],
                            capture_output=True, text=True, encoding='utf-8', check=True)
    return tuple(ToolDefinition.model_validate(tool) for tool in json.loads(result.stdout))


@pytest.fixture
def typed_policy(typed_catalog):
    return Policy(typed_catalog)


@pytest.fixture
def binding():
    return OperationContext(run_id=new_id(), document_id='fixture-b')


def test_typed_catalog_excludes_code_and_strips_host_fields_from_every_union_branch(typed_policy, binding):
    tools = {tool.name: tool for tool in typed_policy.catalog(binding)}
    assert {'corel_create_batch', 'corel_update_batch', 'corel_create_shapes', 'corel_edit_curve', 'corel_find_text', 'corel_preflight',
            'corel_import', 'corel_manage_pages', 'corel_layout_shapes', 'corel_describe_api',
            'corel_measure_shapes'} <= tools.keys()
    assert not {'corel_execute_vba', 'corel_build_vba', 'corel_prune_artifacts'} & tools.keys()
    for name, tool in tools.items():
        def check(schema):
            assert not {'request_id', 'document_id', 'deadline_seconds', 'sha256', 'size_bytes'} & schema.get('properties', {}).keys(), name
            for keyword in ('oneOf', 'anyOf', 'allOf'):
                for branch in schema.get(keyword, []):
                    check(branch)
        check(tool.input_schema)
    schema = tools['corel_transform_shapes'].input_schema
    assert not list(Draft202012Validator(schema).iter_errors({
        'action': 'move', 'page_index': 2, 'static_ids': [1001], 'dx_mm': 5, 'dy_mm': 7}))


async def test_union_schema_keeps_geometry_and_frozen_document_with_postchecks(typed_policy, typed_catalog, binding):
    args = {'action': 'move', 'page_index': 2, 'static_ids': [1001], 'dx_mm': 5, 'dy_mm': 7}
    operation = await typed_policy.prepare('corel_transform_shapes', args, binding)
    assert operation.arguments['document_id'] == binding.document_id
    assert operation.arguments['page_index'] == 2 and operation.corel_request_id
    assert operation.required_postchecks == ('corel_inspect_document', 'corel_preview')
    actual = next(tool for tool in typed_catalog if tool.name == operation.tool).input_schema
    assert not list(Draft202012Validator(actual).iter_errors(operation.arguments))
    assert (await typed_policy.classify(operation, binding)).decision == 'allow'
    changed = binding.model_copy(update={'document_id': 'fixture-a'})
    assert (await typed_policy.classify(operation, changed)).decision == 'deny'
    assert args == {'action': 'move', 'page_index': 2, 'static_ids': [1001], 'dx_mm': 5, 'dy_mm': 7}


async def test_read_only_and_layout_have_conservative_request_ids(typed_policy, binding):
    for tool, args in [('corel_inspect_curve', {'page_index': 1, 'static_id': 1001}),
                       ('corel_find_text', {'page_index': 1, 'query': 'Ёж', 'case_sensitive': True}),
                       ('corel_preflight', {'page_index': 1, 'static_ids': [1001], 'checks': [{'kind': 'text_present'}]})]:
        operation = await typed_policy.prepare(tool, args, binding)
        assert operation.corel_request_id is None and not operation.required_postchecks
    layout = await typed_policy.prepare('corel_layout_shapes', {'page_index': 1, 'static_ids': [1001]}, binding)
    assert layout.corel_request_id and layout.required_postchecks == ('corel_inspect_document', 'corel_preview')
    with pytest.raises(AgentError):
        await typed_policy.prepare('corel_layout_shapes', {'page_index': 1, 'static_ids': [1001], 'dry_run': False}, binding)
    with pytest.raises(AgentError):
        await typed_policy.prepare('corel_edit_curve', {'page_index': 1, 'static_id': 1001, 'action': 'close', 'subpath_index': 1}, binding)


async def test_import_hash_size_and_identity_are_bound_by_host_and_rechecked(typed_policy, binding, tmp_path):
    source = tmp_path / 'Ёж.svg'
    source.write_text('<svg xmlns="http://www.w3.org/2000/svg"/>', encoding='utf-8')
    args = {'path': str(source), 'format': 'svg', 'page_index': 1, 'layer_id': 'layer', 'x_mm': 0, 'y_mm': 0}
    operation = await typed_policy.prepare('corel_import', args, binding)
    assert operation.arguments['sha256'] == hashlib.sha256(source.read_bytes()).hexdigest()
    assert operation.arguments['size_bytes'] == source.stat().st_size and operation.file_fingerprint
    assert (await typed_policy.classify(operation, binding)).decision == 'allow'
    source.write_text('changed', encoding='utf-8')
    assert (await typed_policy.classify(operation, binding)).decision == 'deny'
    for field, value in [('sha256', '0' * 64), ('size_bytes', 12)]:
        with pytest.raises(AgentError) as caught:
            await typed_policy.prepare('corel_import', {**args, field: value}, binding)
        assert caught.value.code == 'BINDING_OVERRIDE'


async def test_old_prepared_code_is_readable_but_denied_even_with_current_digest(typed_policy, binding):
    from corel_agent.contracts import PreparedOperation
    operation = PreparedOperation(operation_id=new_id(), run_id=binding.run_id, tool='corel_execute_vba',
                                  arguments={'code': 'historical'}, digest='0' * 64)
    operation = operation.model_copy(update={'digest': Policy.operation_digest(operation)})
    assert (await typed_policy.classify(operation, binding)).decision == 'deny'
