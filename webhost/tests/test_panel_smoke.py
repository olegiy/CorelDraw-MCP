import importlib
import json
import shutil
import sys

import pytest
from conftest import ROOT

from corel_agent.contracts import AgentError
from corel_agent.corel_mcp import CorelMcp
from corel_agent.owned_stdio import OwnedStdio
from corel_agent.storage_acl import ensure_private


def smoke():
    return importlib.import_module('panel_smoke')


@pytest.mark.parametrize('change', ['approved', 'consent', 'operation_digest', 'extra_field'])
def test_live_fixture_requires_exact_reviewed_scope(tmp_path, change):
    module = smoke()
    data = module.fixture_template(str(tmp_path))
    data['approved'] = True
    if change == 'approved':
        data['approved'] = False
    elif change == 'consent':
        data['discard_created_documents'] = False
    elif change == 'operation_digest':
        data['operations_sha256']['geometry'] = '0' * 64
    else:
        data['document_id'] = 'user-drawing'
    path = tmp_path / 'fixture.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    with pytest.raises(AgentError) as caught:
        module.load_fixture(path)
    assert caught.value.code == 'LIVE_FIXTURE_INVALID'


def test_shared_output_root_is_rejected_before_native_start(tmp_path):
    module = smoke()
    data = module.fixture_template(str(tmp_path))
    data['approved'] = True
    path = tmp_path / 'fixture.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    with pytest.raises(AgentError) as caught:
        module.load_fixture(path)
    assert caught.value.code == 'LIVE_FIXTURE_INVALID'


def test_fixture_snapshot_retains_digest_of_exact_reviewed_bytes(tmp_path):
    import hashlib

    module = smoke()
    root = tmp_path / 'private'
    ensure_private(root)
    data = module.fixture_template(str(root))
    data['approved'] = True
    path = tmp_path / 'fixture.json'
    content = json.dumps(data).encode('utf-8')
    path.write_bytes(content)
    fixture = module.load_fixture(path)
    assert fixture._source_sha256 == hashlib.sha256(content).hexdigest()


def test_unc_fixture_is_denied_without_filesystem_access(monkeypatch):
    from pathlib import Path

    module = smoke()
    opened = []
    monkeypatch.setattr(module, 'assert_no_links', lambda path: None)

    def no_open(path, *args, **kwargs):
        opened.append(path)
        raise OSError('No network/file access in this fixture')

    monkeypatch.setattr(Path, 'open', no_open)
    with pytest.raises(AgentError):
        module.load_fixture(r'\\server\share\fixture.json')
    assert opened == []


async def test_optimized_python_cannot_skip_smoke_safety_assertions(tmp_path):
    import asyncio

    root = tmp_path / 'private'
    ensure_private(root)
    script = '''import asyncio,sys
sys.path.insert(0, "tests")
from panel_smoke import fixture_template, validate_fixture, run_smoke
from corel_agent.contracts import AgentError
data = fixture_template(sys.argv[1]); data["approved"] = True
def forbidden_factory(directory):
    raise RuntimeError("executor reached")
async def check():
    try:
        await run_smoke(validate_fixture(data), mcp_factory=forbidden_factory, authority="fixture")
    except AgentError as error:
        if error.code == "SMOKE_ASSERTIONS_REQUIRED":
            return
    raise RuntimeError("optimized smoke was admitted")
asyncio.run(check())
'''
    process = await asyncio.create_subprocess_exec(sys.executable, '-O', '-c', script, str(root),
        cwd=str(ROOT / 'webhost'))
    assert await process.wait() == 0


async def test_fixture_template_cli_works_without_install_or_corel(tmp_path):
    import asyncio

    process = await asyncio.create_subprocess_exec(sys.executable, 'tests/panel_smoke.py',
        '--output-directory', str(tmp_path), cwd=str(ROOT / 'webhost'),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    output, error = await process.communicate()
    assert process.returncode == 0, error.decode()
    template = json.loads(output)
    assert template['approved'] is False
    assert template['output_directory'] == str(tmp_path)
    assert len(template['operations_sha256']) == 1
    assert not list(tmp_path.iterdir())


@pytest.mark.parametrize('mode', ['success', 'bad_png', 'drawing_failure', 'cleanup_failure', 'unknown_creation',
                                  'retained_gms', 'baseline_failure', 'localized_version', 'wrong_version'])
async def test_http_smoke_preserves_baseline_and_closes_only_created_documents(tmp_path, mode):
    module = smoke()
    transports = []

    def mcp_factory(directory):
        args = [str(ROOT / 'webhost/tests/fixtures/panel-server.mjs'), '--panel-smoke', '--typed']
        if mode != 'success':
            args.append('--smoke-' + mode)
        transport = OwnedStdio(command=shutil.which('node'), args=args,
            directory=directory, env={'CORELDRAW_MCP_RUNS': str(directory / 'runs')})
        transports.append(transport)
        return CorelMcp(transport=transport, poll_interval=0.01)

    root = tmp_path / 'private'
    ensure_private(root)
    data = module.fixture_template(str(root))
    data['approved'] = True
    path = tmp_path / 'fixture.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    fixture = module.load_fixture(path)
    proof = await module.run_smoke(fixture, mcp_factory=mcp_factory, authority='fixture')
    assert proof['ok'] is (mode in {'success', 'localized_version'}), {'failure_code': proof['failure_code'], 'runs': proof['runs']}
    assert proof['authority'] == 'fixture'
    if mode in {'baseline_failure', 'wrong_version'}:
        assert proof['baseline_complete'] is False and proof['failure_stage'] == 'tool:corel_status'
        calls = [json.loads(line) for line in (transports[0].marker.path.parent / 'runs/calls.jsonl').read_text().splitlines()]
        assert [call['tool'] for call in calls] == ['corel_status']
        return
    if mode == 'unknown_creation':
        assert proof['unresolved_creations'] and proof['cleanup_state'] == 'blocked'
        calls = [json.loads(line) for line in (transports[0].marker.path.parent / 'runs/calls.jsonl').read_text().splitlines()]
        assert [call['tool'] for call in calls].count('corel_create_document') == 1
        assert not any(call['tool'] in {'corel_close_document', 'corel_create_shapes'} for call in calls)
        assert transports[0].returncode == 0
        return
    assert len(proof['created_document_ids']) == 2
    if mode == 'retained_gms':
        assert proof['cleanup_state'] == 'blocked'
        assert proof['remaining_document_ids'] == proof['created_document_ids']
        assert proof['retained_projects'][0]['name'].startswith('CorelMcp_fixture_')
        assert any(row['status'] == 'halted' for row in proof['runs'])
        calls = [json.loads(line) for line in (transports[0].marker.path.parent / 'runs/calls.jsonl').read_text().splitlines()]
        assert not any(call['tool'] == 'corel_close_document' for call in calls)
        assert len([call for call in calls if call['tool'] == 'corel_create_shapes']) == 1
        return
    if mode == 'cleanup_failure':
        assert proof['remaining_document_ids'] == proof['created_document_ids']
        assert proof['failure_code'] and proof['documents_after'] is None
        calls = [json.loads(line) for line in (transports[0].marker.path.parent / 'runs/calls.jsonl').read_text(encoding='utf-8').splitlines()]
        assert not any(call['tool'] == 'corel_close_document' for call in calls)
        assert len([call for call in calls if call['tool'] == 'corel_create_shapes']) == 1
        return
    assert proof['remaining_document_ids'] == []
    assert proof['documents_before'] == proof['documents_after']
    assert proof['documents_before'] and {doc['document_id'] for doc in proof['documents_before']} == {'fixture-a', 'fixture-b'}
    assert transports[0].returncode == 0
    report = json.loads((module.Path(proof['directory']) / 'verification.json').read_text(encoding='utf-8'))
    assert report['ok'] is proof['ok']
    calls = [json.loads(line) for line in (transports[0].marker.path.parent / 'runs/calls.jsonl').read_text(encoding='utf-8').splitlines()]
    assert [call['args']['document_id'] for call in calls if call['tool'] == 'corel_close_document'] == list(reversed(proof['created_document_ids']))
    assert all(call['args']['document_id'] in proof['created_document_ids'] for call in calls
               if call['tool'] == 'corel_create_shapes')
    if mode in {'success', 'localized_version'}:
        assert len(proof['previews']) == 2
        assert proof['empty_selection_code'] == 'EMPTY_SELECTION'
        assert proof['baseline_inspections_before'] == proof['baseline_inspections_after']
    else:
        assert proof['failure_code']


async def test_repeated_cancellation_drains_native_and_retains_failure_evidence(tmp_path):
    import asyncio

    module = smoke()
    root = tmp_path / 'private'
    ensure_private(root)
    data = module.fixture_template(str(root))
    data['approved'] = True
    path = tmp_path / 'fixture.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    transports = []

    def factory(directory):
        transport = OwnedStdio(command=shutil.which('node'),
            args=[str(ROOT / 'webhost/tests/fixtures/panel-server.mjs'), '--panel-smoke', '--typed', '--slow'],
            directory=directory, env={'CORELDRAW_MCP_RUNS': str(directory / 'runs')})
        transports.append(transport)
        return CorelMcp(transport=transport, poll_interval=0.01)

    task = asyncio.create_task(module.run_smoke(module.load_fixture(path), mcp_factory=factory, authority='fixture'))
    async with asyncio.timeout(30):
        while True:
            if transports:
                log = transports[0].marker.path.parent / 'runs/calls.jsonl'
                if log.exists() and 'corel_create_shapes' in log.read_text(encoding='utf-8'):
                    break
            await asyncio.sleep(0.01)
    task.cancel()
    asyncio.get_running_loop().call_soon(task.cancel)
    with pytest.raises(asyncio.CancelledError):
        await task
    report = json.loads(next(root.glob('*/verification.json')).read_text(encoding='utf-8'))
    assert report['ok'] is False and report['failure_code'] == 'SMOKE_CANCELLED'
    assert report['remaining_document_ids'] == report['created_document_ids']
    assert len(report['remaining_document_ids']) == 2
    assert report['runs'][-1]['reports']
    assert transports[0].returncode == 0
