"""Explicit real-model/Corel/browser acceptance; never collected by pytest."""
import asyncio
import json
import os
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from corel_agent.contracts import ProcessIdentity, new_id  # noqa: E402
from corel_agent.launcher import signal_existing  # noqa: E402
from corel_agent.owned_stdio import atomic_json, identity_alive  # noqa: E402
from corel_agent.settings import Settings  # noqa: E402
from corel_agent.storage_acl import ensure_private  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
MODEL_CODE = '''Option Explicit
Public Function Main() As String
    Dim s As Shape
    Dim deadline As Date
    Set s = ActiveDocument.ActiveLayer.CreateRectangle2(10, 10, 20, 30)
    s.Name = "M3_agent_rectangle"
    s.CreateSelection
    deadline = DateAdd("s", 3, Now)
    Do While Now < deadline
        DoEvents
    Loop
    Main = "{""created"":true,""static_id"":" & CStr(s.StaticID) & "}"
End Function'''
WAIT_CODE = '''Option Explicit
Public Function Main() As String
    Dim deadline As Date
    deadline = DateAdd("s", 3, Now)
    Do While Now < deadline
        DoEvents
    Loop
    Main = "{""drained"":true}"
End Function'''


async def main(report_root):
    profiles = await Settings(Path(os.environ['LOCALAPPDATA']) / 'CorelDrawAgent').list_profiles()
    assert len(profiles) == 1 and profiles[0].context_approved
    # The explicit one-Run authorization excludes images even if the operator
    # changes their usual profile while preparing acceptance. Never edit it.
    profile = profiles[0].model_copy(update={'vision': False, 'send_preview': False})
    directory = report_root / ('m3-http-' + new_id())
    ensure_private(directory)
    atomic_json(directory / 'profiles.json', {'schema_version': 1, 'profiles': [profile.model_dump(mode='json')]})
    exports = directory / 'exports'
    ensure_private(exports)
    proof = {'schema_version': 1, 'ok': False, 'authority': 'live', 'checks': [],
             'model_runs_started': 0, 'directory': str(directory), 'created_documents': []}
    server = browser = None
    native_identity = None
    doc_ids = []
    root_id = new_id()
    stage = 'startup'

    def save():
        atomic_json(directory / 'verification.json', proof)

    def passed(name):
        proof['checks'].append(name)
        print('M3_PASS ' + name, flush=True)
        save()

    async def start_server():
        process = await asyncio.create_subprocess_exec(sys.executable, str(ROOT / 'webhost/tests/m3_live_server.py'),
            str(directory), env={**os.environ, 'CORELDRAW_AGENT_M3_LIVE': '1'},
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        identity = None
        async with asyncio.timeout(40):
            while line := await process.stdout.readline():
                message = json.loads(line)
                if 'native_identity' in message:
                    identity = ProcessIdentity.model_validate(message['native_identity'])
                if 'bootstrap' in message:
                    assert identity is not None
                    return process, identity, message['bootstrap']
        raise AssertionError('LIVE_LAUNCHER_NOT_READY')

    async def rpc(op, **payload):
        browser.stdin.write((json.dumps({'op': op, **payload}) + '\n').encode())
        await browser.stdin.drain()
        async with asyncio.timeout(30):
            line = await browser.stdout.readline()
        response = json.loads(line)
        assert response['ok'], 'BROWSER_' + response.get('error', 'FAILED')
        return response['value']

    async def api(method, path, body=None, statuses=(200, 202)):
        response = await rpc('api', method=method, path=path, body=body)
        assert response['status'] in statuses, f"API_{response['status']}_{response['body'].get('code', 'FAILED')}"
        return response['body']

    async def wait_run(run_id, *, pending=False):
        async with asyncio.timeout(180):
            while True:
                snapshot = await api('GET', '/runs/' + run_id)
                if pending and snapshot.get('pending_confirmation'):
                    return snapshot
                if snapshot['status'] in {'completed', 'failed', 'stopped', 'interrupted', 'halted', 'uncertain'}:
                    return snapshot
                await asyncio.sleep(0.05)

    async def manual(tool, arguments=None, document_id=None, *, defer=False):
        body = {'client_request_id': new_id(), 'conversation_id': new_id(), 'kind': 'manual',
                'tool': tool, 'arguments': arguments or {}, 'document_id': document_id,
                'read_only': tool in {'corel_status', 'corel_inspect_document', 'corel_preview'},
                'output_root_id': root_id}
        accepted = await api('POST', '/actions', body)
        if defer:
            return accepted, body
        terminal = await wait_run(accepted['id'])
        assert terminal['status'] == 'completed', 'MANUAL_' + terminal['status']
        operations = (await api('GET', f"/runs/{accepted['id']}/operations"))['items']
        result = next(row['result']['report']['result'] for row in operations if row.get('result'))
        return result

    def journal():
        path = directory / 'native-journal.jsonl'
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    async def confirm(snapshot, approved=True):
        pending = snapshot['pending_confirmation']
        return await api('POST', '/runs/' + snapshot['id'] + '/confirm', {
            'confirmation_id': pending['confirmation_id'], 'operation_digest': pending['operation_digest'],
            'approved': approved})

    try:
        server, native_identity, url = await start_server()
        browser = await asyncio.create_subprocess_exec(shutil.which('node'), str(ROOT / 'webhost/tests/m3_browser.mjs'),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        await rpc('open', url=url)
        await api('PUT', '/workspace', {'output_roots': [{'id': root_id, 'path': str(exports)}]})
        passed('real_launcher_bootstrap_cookie_no_web_storage')
        before = await manual('corel_status')
        proof['baseline_documents'] = before['documents']
        for _ in range(2):
            created = await manual('corel_create_document', {'width_mm': 100, 'height_mm': 80})
            doc_ids.append(created['document_id'])
        proof['created_documents'] = doc_ids.copy()
        passed('two_temporary_live_documents')

        stage = 'real_model'
        prompt = ('Приёмка M3. Работай только в выбранном временном документе. '
            'Выполни ровно один native tool call corel_execute_vba с ТОЧНО следующим кодом, '
            'без добавлений и без других операций, затем заверши ответ по-русски. '
            'Не создавай и не закрывай документы, не обращайся к файлам/ОС. Код:\n' + MODEL_CODE)
        with (report_root / 'model-run-budget.json').open('x', encoding='utf-8') as budget:
            json.dump({'reserved': True, 'maximum_runs': 1, 'profile_id': profiles[0].id}, budget)
        proof['model_runs_started'] = 1
        save()
        accepted = await rpc('agent', document_id=doc_ids[0], message=prompt)
        model_id = accepted['id']
        snapshot = await wait_run(model_id, pending=True)
        assert snapshot.get('pending_confirmation'), 'REAL_MODEL_NO_REVIEWED_NATIVE_CALL'
        pending = snapshot['pending_confirmation']
        assert pending['operation']['tool'] == 'corel_execute_vba'
        assert pending['operation']['arguments']['code'].replace('\r\n', '\n').strip() == MODEL_CODE.strip(), 'MODEL_CODE_NOT_REVIEWED'
        await rpc('approve', run_id=model_id, pending=pending, code=MODEL_CODE)
        await rpc('close-tabs')
        snapshot = await wait_run(model_id)
        assert snapshot['status'] == 'completed', 'REAL_MODEL_' + snapshot['status']
        proof['model_snapshot'] = snapshot
        assert snapshot['counters']['turns'] <= 10
        mutations = [row for row in journal() if row['tool'] == 'corel_execute_vba']
        assert len(mutations) == 1
        actual = await manual('corel_inspect_document', {'include_text': True}, doc_ids[0])
        assert actual['total_shapes'] == 1
        shape = actual['shapes'][0]
        assert shape['name'] == 'M3_agent_rectangle'
        assert abs(shape['bounds_mm']['width'] - 20) < 0.001 and abs(shape['bounds_mm']['height'] - 30) < 0.001
        untouched = await manual('corel_inspect_document', {}, doc_ids[1])
        assert untouched['total_shapes'] == 0
        passed('real_vault_provider_native_call_exact_code_single_dispatch_graphical_effect')
        passed('closed_tabs_do_not_cancel_live_native_job')
        signal_existing(directory, 'open')
        url = json.loads(await asyncio.wait_for(server.stdout.readline(), 10))['bootstrap']
        await rpc('open', url=url)
        await rpc('history', label='agent · completed', status='completed', screenshot=str(directory / 'model-result.png'))
        passed('real_model_history_reload_mobile_no_replay')

        stage = 'manual_safety'
        for doc_id, name in zip(doc_ids, ('model.cdr', 'sentinel.cdr'), strict=True):
            await manual('corel_save_document', {'path': str(exports / name)}, doc_id)
        for target in ('page', 'selection'):
            preview = await manual('corel_preview', {'target': target, 'max_size': 256}, doc_ids[0])
            assert preview['path']
        await manual('corel_export', {'path': str(exports / 'model.pdf'), 'format': 'pdf'}, doc_ids[0])
        passed('http_live_save_preview_page_selection_pdf_export')
        accepted, body = await manual('corel_execute_vba', {'code': WAIT_CODE}, doc_ids[0], defer=True)
        waiting = await wait_run(accepted['id'], pending=True)
        repeated = await api('POST', '/actions', body)
        assert repeated['id'] == accepted['id']
        await api('POST', '/actions', {**body, 'arguments': {'code': MODEL_CODE}}, statuses=(409,))
        await confirm(waiting)
        async with asyncio.timeout(15):
            while not (await api('GET', '/runs/' + accepted['id'])).get('job_id'):
                await asyncio.sleep(0.01)
        await api('POST', '/runs/' + accepted['id'] + '/stop')
        terminal = await wait_run(accepted['id'])
        assert terminal['status'] == 'stopped'
        operations = (await api('GET', f"/runs/{accepted['id']}/operations"))['items']
        assert any(row.get('result', {}).get('report', {}).get('status') == 'succeeded' for row in operations)
        passed('http_idempotency_conflict_stop_drains_actual_native_job')

        stage = 'controlled_idle_host_crash'
        accepted, _ = await manual('corel_execute_vba', {'code': WAIT_CODE}, doc_ids[0], defer=True)
        waiting = await wait_run(accepted['id'], pending=True)
        assert waiting['job_id'] is None
        health = await api('GET', '/health')
        assert health['active_job_id'] is None
        before_crash = len(journal())
        await rpc('close-tabs')
        server.kill()  # Explicitly authorized test-owned host, never Corel/MCP/worker.
        await server.wait()
        server = None
        async with asyncio.timeout(30):
            while identity_alive(native_identity) is True:
                await asyncio.sleep(0.05)
        assert identity_alive(native_identity) is False, 'OLD_NATIVE_IDENTITY_UNCERTAIN'
        server, native_identity, url = await start_server()
        await rpc('open', url=url, reset=True)
        recovered = await api('GET', '/runs/' + accepted['id'])
        assert recovered['status'] == 'interrupted' and recovered['pending_confirmation'] is None
        assert len(journal()) == before_crash
        saved_model = await api('GET', '/runs/' + model_id)
        assert saved_model['status'] == 'completed'
        refreshed = await manual('corel_status')
        expected_paths = {str(exports / 'model.cdr'), str(exports / 'sentinel.cdr')}
        doc_ids = [doc['document_id'] for doc in refreshed['documents'] if doc['path'] in expected_paths]
        assert len(doc_ids) == 2
        passed('real_host_crash_no_native_job_natural_mcp_exit_interrupted_no_replay')
        for doc_id in doc_ids.copy():
            await manual('corel_close_document', {}, doc_id)
            doc_ids.remove(doc_id)
        after = await manual('corel_status')
        def comparable(docs):
            return sorted([{key: value for key, value in doc.items() if key != 'document_id'} for doc in docs], key=lambda doc: (doc['path'], doc['name']))
        assert comparable(before['documents']) == comparable(after['documents'])
        passed('live_user_document_baseline_preserved_after_restart')
        proof['ok'] = True
    except BaseException as error:
        proof['failed_stage'] = stage
        proof['failure_type'] = type(error).__name__
        print('M3_FAIL ' + stage + ' ' + type(error).__name__, flush=True)
        raise
    finally:
        save()
        if browser:
            try:
                if server and server.returncode is None:
                    for doc_id in doc_ids.copy():
                        try:
                            accepted, _ = await manual('corel_close_document', {'discard_changes': True}, doc_id, defer=True)
                            waiting = await wait_run(accepted['id'], pending=True)
                            if waiting.get('pending_confirmation'):
                                assert waiting['pending_confirmation']['operation']['document_id'] == doc_id
                                await confirm(waiting)
                            terminal = await wait_run(accepted['id'])
                            assert terminal['status'] == 'completed'
                            doc_ids.remove(doc_id)
                        except Exception:
                            proof['cleanup_blocked'] = True
                await rpc('exit')
                await asyncio.wait_for(browser.wait(), 15)
            except Exception:
                proof['browser_cleanup_failed'] = True
        if server and server.returncode is None:
            signal_existing(directory, 'stop')
            await asyncio.wait_for(server.wait(), 30)
        proof['native_exited'] = native_identity is None or identity_alive(native_identity) is False
        proof['owners_released'] = not (directory / 'runtime.json').exists() and not (directory / 'mcp-owner.json').exists()
        proof['created_documents_closed'] = not doc_ids
        proof['ok'] = proof['ok'] and proof['native_exited'] and proof['owners_released'] and not doc_ids
        save()
        print('M3_HTTP_REPORT=' + str(directory / 'verification.json'), flush=True)


if __name__ == '__main__':
    if os.environ.get('CORELDRAW_AGENT_M3_LIVE') != '1':
        raise SystemExit('Explicit real-model and Corel live opt-in required.')
    asyncio.run(main(Path(sys.argv[1]).resolve()))
