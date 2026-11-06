"""Opt-in HTTP/Runtime/Corel smoke. Import and fixture templates never launch Corel."""

import asyncio
import hashlib
import io
import json
import os
import shutil
import sys
from pathlib import Path

if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import httpx
from PIL import Image
from pydantic import BaseModel, ConfigDict, StrictBool

from corel_agent.access import Access
from corel_agent.app import create_app
from corel_agent.assets import Assets, assert_no_links
from corel_agent.contracts import TERMINAL_STATUSES, AgentError, RunCommand, SafetyBarrier, new_id
from corel_agent.corel_mcp import CorelMcp
from corel_agent.owned_stdio import OwnedStdio, atomic_json
from corel_agent.policy import PathGuard
from corel_agent.runtime import Runtime
from corel_agent.storage_acl import ensure_private
from corel_agent.store import Store

ROOT = Path(__file__).resolve().parents[2]
HELPERS = '''Private Function JsonString(ByVal value As String) As String
    Dim i As Long, code As Long, ch As String, result As String
    For i = 1 To Len(value)
        ch = Mid$(value, i, 1)
        code = AscW(ch)
        If code = 34 Or code = 92 Then
            result = result & Chr$(92) & ch
        ElseIf code >= 0 And code < 32 Then
            result = result & Chr$(92) & "u" & Right$("0000" & Hex$(code), 4)
        Else
            result = result & ch
        End If
    Next i
    JsonString = Chr$(34) & result & Chr$(34)
End Function
Private Function ProjectsJson() As String
    Dim i As Long, result As String
    If Application.GMSManager.Projects.Count > 500 Then Err.Raise 5, "PanelSmoke", "GMS limit"
    result = "["
    For i = 1 To Application.GMSManager.Projects.Count
        If i > 1 Then result = result & ","
        result = result & JsonString(Application.GMSManager.Projects.Item(i).Name)
    Next i
    ProjectsJson = result & "]"
End Function
'''
GEOMETRY = '''Option Explicit
' CorelMcpPanelSmoke: geometry v1
Public Function Main() As String
    Dim s As Shape, projects As String
    projects = ProjectsJson()
    ActiveDocument.Unit = cdrMillimeter
    Set s = ActiveLayer.CreateRectangle2(10, 10, 20, 30)
    s.Name = "CorelMcp panel smoke"
    s.CreateSelection
    Main = "{""projects"":" & projects & ",""static_id"":" & CStr(s.StaticID) & "}"
End Function
''' + HELPERS
INVENTORY = '''Option Explicit
' CorelMcpPanelSmoke: inventory v1
Public Function Main() As String
    Main = "{""projects"":" & ProjectsJson() & "}"
End Function
''' + HELPERS
CODES = {'geometry': GEOMETRY, 'inventory': INVENTORY}


class LiveFixture(BaseModel):
    model_config = ConfigDict(extra='forbid', frozen=True)
    schema_version: int
    approved: StrictBool
    create_temporary_documents: StrictBool
    execute_reviewed_vba: StrictBool
    discard_created_documents: StrictBool
    output_directory: str
    vba_sha256: dict[str, str]
    _source_sha256: str | None = None


def fixture_template(output_directory):
    return {'schema_version': 1, 'approved': False, 'create_temporary_documents': True,
            'execute_reviewed_vba': True, 'discard_created_documents': True,
            'output_directory': output_directory,
            'vba_sha256': {name: hashlib.sha256(code.encode('utf-8')).hexdigest() for name, code in CODES.items()}}


def validate_fixture(value):
    fixture = LiveFixture.model_validate(value)
    if (fixture.schema_version != 1 or not all((fixture.approved, fixture.create_temporary_documents,
            fixture.execute_reviewed_vba, fixture.discard_created_documents))
            or fixture.vba_sha256 != fixture_template(fixture.output_directory)['vba_sha256']):
        raise ValueError('Unreviewed fixture')
    root = PathGuard().directory(fixture.output_directory)
    local = os.environ.get('LOCALAPPDATA')
    if not local or root == Path(local).resolve() or not root.is_relative_to(Path(local).resolve()):
        raise ValueError('Output root must be inside the current user profile')
    ensure_private(root)
    return fixture.model_copy(update={'output_directory': str(root)})


def load_fixture(path):
    def unique(pairs):
        data = {}
        for key, value in pairs:
            if key in data:
                raise ValueError('Duplicate fixture field')
            data[key] = value
        return data
    try:
        path = PathGuard().path(str(path))
        assert_no_links(path)
        with path.open('rb') as source:
            before = os.fstat(source.fileno())
            if not 0 < before.st_size <= 16384 or before.st_nlink != 1:
                raise ValueError('Invalid fixture')
            content = source.read(16385)
            after = os.fstat(source.fileno())
            current = path.stat()
            assert_no_links(path)
            if (before.st_size != len(content) or before.st_mtime_ns != after.st_mtime_ns
                    or (before.st_dev, before.st_ino) != (current.st_dev, current.st_ino)):
                raise ValueError('Fixture changed')
        fixture = validate_fixture(json.loads(content.decode('utf-8'), object_pairs_hook=unique))
        fixture._source_sha256 = hashlib.sha256(content).hexdigest()
        return fixture
    except Exception:
        raise AgentError('LIVE_FIXTURE_INVALID', 'Нужен проверенный локальный JSON fixture с явным согласием и точными SHA-256 VBA.', 403) from None


async def run_smoke(fixture, *, mcp_factory=None, authority='live'):
    if not __debug__:
        raise AgentError('SMOKE_ASSERTIONS_REQUIRED', 'Smoke запрещён с Python -O: проверки безопасности должны быть включены.', 403)
    fixture = validate_fixture(fixture)
    if mcp_factory is None:
        if authority != 'live' or os.environ.get('CORELDRAW_AGENT_LIVE') != '1' or not fixture._source_sha256:
            raise AgentError('LIVE_OPT_IN_REQUIRED', 'Реальный smoke требует CORELDRAW_AGENT_LIVE=1 и отдельного разрешения.', 403)
        def mcp_factory(directory):
            transport = OwnedStdio(command=shutil.which('node'), args=[str(ROOT / 'dist/index.js')],
                directory=directory, cwd=str(ROOT), env={'CORELDRAW_MCP_RUNS': str(directory / 'runs')})
            return CorelMcp(transport=transport)
    elif authority != 'fixture':
        raise AgentError('SMOKE_AUTHORITY_INVALID', 'Тестовый executor не является live-доказательством.', 403)

    directory = Path(fixture.output_directory) / f'CorelMcp-panel-smoke-{new_id()}'
    ensure_private(directory)
    host = directory / 'host'
    ensure_private(host)
    store, mcp = Store(host), mcp_factory(host)
    runtime, client = None, None
    proof = {'schema_version': 1, 'authority': authority, 'ok': False, 'directory': str(directory),
             'created_document_ids': [], 'remaining_document_ids': [], 'runs': [], 'previews': [],
             'documents_before': None, 'documents_after': None, 'failure_code': None,
             'baseline_inspections_before': None, 'baseline_inspections_after': None,
             'unresolved_creations': [], 'cleanup_state': 'not_started', 'stage': 'initialization',
             'failure_stage': None, 'fixture_sha256': fixture._source_sha256,
             'retained_projects': [], 'baseline_complete': False}
    created = proof['created_document_ids']
    remaining = proof['remaining_document_ids']
    baseline_ids = set()
    access = Access()

    async def get(path):
        response = await client.get('/api/v1' + path)
        assert response.status_code == 200
        return response.json()

    async def capture(row):
        run_id = row['id']
        snapshot = await get('/runs/' + run_id)
        operations, offset = [], 0
        while True:
            page = await get(f'/runs/{run_id}/operations?offset={offset}')
            operations.extend(page['items'])
            if page['next_offset'] is None:
                break
            offset = page['next_offset']
        reports = [await get('/operations/' + op['id'] + '/report') for op in operations]
        for op, item in zip(operations, reports, strict=True):
            if (op['prepared']['tool'] == 'corel_execute_vba' and
                    (op['job_id'] or op['state'] == 'dispatching' or (item['result'] or {}).get('outcome') == 'unknown')):
                cleanup = (item['report'] or {}).get('cleanup', {})
                if cleanup.get('project_retained') is not False:
                    if not any(project['operation_id'] == op['id'] for project in proof['retained_projects']):
                        proof['retained_projects'].append({'operation_id': op['id'], 'name': cleanup.get('project_name'),
                                                          'reason': 'GMS_UNLOAD_NOT_PROVEN'})
                    if await store.get_barrier() is None:
                        await store.set_barrier(SafetyBarrier(reason='GMS_UNLOAD_NOT_PROVEN', run_id=run_id,
                            operation_id=op['id'], job_id=op['job_id']), status='halted')
        row.update(status=snapshot['status'], reports=[{'operation_id': item['operation_id'], 'report': item['report'],
            'result': {key: value for key, value in (item['result'] or {}).items() if key != 'images'}} for item in reports])
        return reports, operations

    async def action(tool, document_id=None, arguments=None, *, expected='completed'):
        proof['stage'] = 'tool:' + tool
        command = RunCommand(client_request_id=new_id(), conversation_id=new_id(), kind='manual',
            tool=tool, document_id=document_id, arguments=arguments or {}, read_only=tool in {
                'corel_status', 'corel_inspect_document', 'corel_preview'})
        creation = None
        if tool == 'corel_create_document':
            creation = {'client_request_id': command.client_request_id, 'run_id': None,
                        'reason': 'CREATE_OUTCOME_NOT_PROVEN'}
            proof['unresolved_creations'].append(creation)
        response = await client.post('/api/v1/actions', json=command.model_dump(mode='json'))
        assert response.status_code == 202
        run_id = response.json()['id']
        if creation:
            creation['run_id'] = run_id
        row = {'id': run_id, 'tool': tool, 'document_id': document_id}
        proof['runs'].append(row)
        async with asyncio.timeout(660):
            while True:
                snapshot = await get('/runs/' + run_id)
                pending = snapshot['pending_confirmation']
                if pending:
                    op = pending['operation']
                    assert document_id in created and op['document_id'] == document_id
                    assert op['tool'] == tool and snapshot['job_id'] is None
                    if tool == 'corel_execute_vba':
                        assert op['arguments']['code'] in CODES.values()
                    else:
                        assert tool == 'corel_close_document' and op['arguments']['discard_changes'] is True
                    response = await client.post('/api/v1/runs/' + run_id + '/confirm', json={
                        'confirmation_id': pending['confirmation_id'], 'operation_digest': pending['operation_digest'], 'approved': True})
                    assert response.status_code == 200
                if snapshot['status'] in TERMINAL_STATUSES:
                    break
                await asyncio.sleep(0.02)
        reports, operations = await capture(row)
        assert operations and operations[0]['prepared']['document_id'] == document_id and operations[0]['prepared']['tool'] == tool
        if tool == 'corel_create_document' and reports and reports[0]['report'].get('status') == 'succeeded':
            new_doc = reports[0]['report']['result']['document_id']
            assert isinstance(new_doc, str) and 0 < len(new_doc) <= 100 and new_doc not in baseline_ids and new_doc not in created
            created.append(new_doc)
            remaining.append(new_doc)
            proof['unresolved_creations'].remove(creation)
        assert snapshot['status'] == expected
        assert reports and reports[0]['report']['cleanup']['completed'] is True
        return reports[0]['report'], run_id

    async def inspect(document_id, page_index=None):
        arguments = {'recursive': True, 'limit': 500}
        if page_index is not None:
            arguments['page_index'] = page_index
        report, _ = await action('corel_inspect_document', document_id, arguments)
        value = report['result']
        assert value['document']['document_id'] == document_id
        assert all(value.get(key) is False for key in ('shapes_truncated', 'pages_truncated', 'layers_truncated',
                   'selection_truncated', 'depth_truncated', 'visited_truncated'))
        assert value['next_offset'] is None
        return value

    async def baseline_inspections():
        result = {}
        for doc in proof['documents_before']:
            pages = doc['pages']
            assert type(pages) is int and 0 < pages <= 32
            result[doc['document_id']] = [await inspect(doc['document_id'], page_index=index)
                                          for index in range(1, pages + 1)]
        return result

    def projects(report):
        cleanup = report['cleanup']
        values = report['result']['value']['projects']
        assert cleanup['project_retained'] is False and values.count(cleanup['project_name']) == 1
        return sorted(name for name in values if name != cleanup['project_name'])

    async def preview(document_id, target):
        report, run_id = await action('corel_preview', document_id, {'target': target, 'page_index': 1, 'max_size': 600})
        assert report['result']['format'] == 'png' and report['result']['range'] == target
        events = (await get('/runs/' + run_id + '/event-log'))['items']
        images = [event['data'] for event in events if event['type'] == 'image']
        assert len(images) == 1 and images[0]['document_id'] == document_id and images[0]['target'] == target
        image = await client.get('/api/v1/images/' + images[0]['image_id'])
        assert image.status_code == 200 and image.headers['content-type'] == 'image/png'
        assert 0 < len(image.content) <= 10 * 1024**2 and image.content.startswith(b'\x89PNG\r\n\x1a\n')
        with Image.open(io.BytesIO(image.content)) as png:
            width, height = png.size
            png.verify()
        assert 0 < width <= 600 and 0 < height <= 600
        # Document IDs are opaque, not filename fragments supplied by Corel.
        path = directory / f'preview-{len(proof["previews"])}.png'
        path.write_bytes(image.content)
        proof['previews'].append({'document_id': document_id, 'target': target, 'path': path.name,
                                 'sha256': hashlib.sha256(image.content).hexdigest(), 'width': width, 'height': height})

    try:
        await store.open()
        await mcp.start()
        runtime = Runtime(store, mcp, Assets(host, store))
        client = httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(runtime=runtime, store=store,
            assets=runtime.assets, access=access)), base_url='http://127.0.0.1:8878', trust_env=False,
            headers={'Origin': 'http://127.0.0.1:8878'})
        response = await client.post('/api/v1/access/bootstrap', headers={'X-Corel-Bootstrap': access.issue_bootstrap()})
        assert response.status_code == 200
        client.headers['X-Corel-CSRF'] = response.json()['csrf_token']
        initial, _ = await action('corel_status')
        assert initial['versions']['coreldraw'].split('.')[0] == '27' and initial['result']['vba']['available'] is True
        assert initial['result']['vba']['compile_available'] is True
        proof['application_state_before'] = initial['result']['application_state']
        proof['active_document_before'] = initial['result']['active_document_id']
        proof['documents_before'] = sorted(initial['result']['documents'], key=lambda doc: doc['document_id'])
        baseline_ids.update(doc['document_id'] for doc in proof['documents_before'])
        assert len(baseline_ids) == len(proof['documents_before']) and len(baseline_ids) <= 32
        proof['baseline_inspections_before'] = await baseline_inspections()
        proof['baseline_complete'] = True
        for _ in range(2):
            await action('corel_create_document', arguments={'width_mm': 100, 'height_mm': 80})
        a, b = created
        b_before = await inspect(b)
        empty, _ = await action('corel_preview', b, {'target': 'selection', 'max_size': 600}, expected='failed')
        assert empty['status'] == 'failed' and empty['error']['code'] == 'EMPTY_SELECTION'
        proof['empty_selection_code'] = empty['error']['code']
        first, _ = await action('corel_execute_vba', a, {'code': GEOMETRY})
        proof['gms_before'] = projects(first)
        assert await inspect(b) == b_before
        second, _ = await action('corel_execute_vba', b, {'code': GEOMETRY})
        assert projects(second) == proof['gms_before']
        for doc_id, execution in ((a, first), (b, second)):
            observed = await inspect(doc_id)
            static_id = execution['result']['value']['static_id']
            assert observed['total_shapes'] == 1 and observed['selection_ids'] == [static_id]
            assert observed['shapes'][0]['static_id'] == static_id
            bounds = observed['shapes'][0]['bounds_mm']
            assert abs(bounds['width'] - 20) < 0.001 and abs(bounds['height'] - 30) < 0.001
            for target in ('page', 'selection'):
                await preview(doc_id, target)
        final_gms, _ = await action('corel_execute_vba', a, {'code': INVENTORY})
        proof['gms_after'] = projects(final_gms)
        assert proof['gms_after'] == proof['gms_before']
        proof['ok'] = True
    except asyncio.CancelledError:
        proof['failure_code'], proof['failure_stage'] = 'SMOKE_CANCELLED', proof['stage']
        raise
    except Exception as error:
        proof['failure_code'] = error.code if isinstance(error, AgentError) else 'SMOKE_CHECK_FAILED'
        proof['failure_stage'] = proof['stage']
    finally:
        async def finalize():
            try:
                proof['cleanup_state'] = 'blocked'
                if (mcp.ready and runtime is not None and not store.failed and not runtime._tasks
                        and not proof['unresolved_creations'] and proof['failure_code'] != 'SMOKE_CANCELLED'
                        and proof['baseline_complete'] and not proof['retained_projects']
                        and await store.get_barrier() is None):
                    proof['cleanup_state'] = 'running'
                    for document_id in reversed(created):
                        try:
                            await action('corel_close_document', document_id, {'discard_changes': True})
                            remaining.remove(document_id)
                        except Exception:
                            proof['ok'], proof['failure_code'] = False, proof['failure_code'] or 'SMOKE_CLEANUP_FAILED'
                            proof['cleanup_state'] = 'partial'
                            break
                    if not remaining:
                        proof['cleanup_state'] = 'completed'
                        after, _ = await action('corel_status')
                        proof['application_state_after'] = after['result']['application_state']
                        proof['active_document_after'] = after['result']['active_document_id']
                        proof['documents_after'] = sorted(after['result']['documents'], key=lambda doc: doc['document_id'])
                        proof['baseline_inspections_after'] = await baseline_inspections()
                        assert proof['documents_after'] == proof['documents_before']
                        assert proof['baseline_inspections_after'] == proof['baseline_inspections_before']
                        assert proof['application_state_after'] == proof['application_state_before']
                        assert proof['active_document_after'] == proof['active_document_before']
                if remaining or proof['unresolved_creations']:
                    proof['ok'], proof['failure_code'] = False, proof['failure_code'] or 'SMOKE_CLEANUP_BLOCKED'
            except Exception:
                proof['ok'], proof['failure_code'] = False, proof['failure_code'] or 'SMOKE_PRESERVATION_FAILED'
                proof['failure_stage'] = proof['failure_stage'] or proof['stage']
            finally:
                try:
                    await (runtime.aclose() if runtime is not None else mcp.aclose())
                    if client is not None and not store.failed:
                        for row in proof['runs']:
                            await capture(row)
                except Exception:
                    proof['ok'], proof['failure_code'] = False, proof['failure_code'] or 'SMOKE_DRAIN_FAILED'
                finally:
                    try:
                        if client is not None:
                            await client.aclose()
                        await store.aclose()
                    except Exception:
                        proof['ok'], proof['failure_code'] = False, proof['failure_code'] or 'SMOKE_STORE_CLOSE_FAILED'
                    finally:
                        atomic_json(directory / 'verification.json', proof)
        cleanup = asyncio.create_task(finalize())
        cancelled = False
        while True:
            try:
                await asyncio.shield(cleanup)
                break
            except asyncio.CancelledError:
                if cleanup.cancelled():
                    raise
                cancelled = True
                proof['ok'], proof['failure_code'] = False, 'SMOKE_CANCELLED'
                proof['failure_stage'] = proof['failure_stage'] or proof['stage']
        if cancelled:
            raise asyncio.CancelledError()
    return proof


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description='Печатать template fixture без запуска CorelDRAW.')
    parser.add_argument('--output-directory', required=True)
    args = parser.parse_args()
    print(json.dumps(fixture_template(args.output_directory), ensure_ascii=False, indent=2))
