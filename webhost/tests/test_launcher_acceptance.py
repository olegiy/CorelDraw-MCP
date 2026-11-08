"""Production launcher/HTTP lifecycle with an owned test-only Node executor."""

import asyncio
import json
import os
import shutil
import sys
from urllib.parse import parse_qs, urlsplit

import httpx
import pytest
from conftest import ROOT, command, implementation

from corel_agent.contracts import AgentError
from corel_agent.corel_mcp import CorelMcp
from corel_agent.owned_stdio import OwnedStdio
from corel_agent.store import Store


async def until(predicate):
    async with asyncio.timeout(10):
        while not predicate():
            await asyncio.sleep(0.01)


async def finish_browser_host(task, *, timeout=10, primary=None):
    try:
        await asyncio.wait_for(asyncio.shield(task), timeout)
    except TimeoutError as timeout_error:
        # Cancel only this test-owned HTTP wait. serve's production finally
        # still shields the fixture MCP drain and exact owner release.
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        except BaseException:
            timeout_error.add_note('TEST_HOST_CLEANUP_FAILED')
        if primary is not None:
            primary.add_note('TEST_HOST_SHUTDOWN_TIMEOUT')
            return
        raise AssertionError('TEST_HOST_SHUTDOWN_TIMEOUT') from timeout_error
    except BaseException:
        if primary is not None:
            primary.add_note('TEST_HOST_CLEANUP_FAILED')
            return
        raise


async def test_browser_harness_timeout_still_drains_its_owned_fixture():
    drained = asyncio.Event()

    async def stalled_http_host():
        try:
            await asyncio.Event().wait()
        finally:
            await asyncio.sleep(0.01)
            drained.set()

    task = asyncio.create_task(stalled_http_host())
    try:
        with pytest.raises(AssertionError, match='TEST_HOST_SHUTDOWN_TIMEOUT'):
            await finish_browser_host(task, timeout=0.01)
        assert task.done() and drained.is_set()
    finally:
        if not task.done():
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task


async def test_browser_harness_cleanup_preserves_primary_failure():
    async def stalled_http_host():
        await asyncio.Event().wait()

    task = asyncio.create_task(stalled_http_host())
    primary = ValueError('fixture browser failure')
    try:
        try:
            raise primary
        finally:
            await finish_browser_host(task, timeout=0.01, primary=primary)
    except BaseException as actual:
        assert actual is primary
    assert task.done()


@pytest.fixture
def launcher_harness(tmp_path, monkeypatch):
    module = implementation('__main__')
    launcher = implementation('launcher')
    state = {'urls': [], 'mcps': [], 'servers': [], 'listeners': []}
    server_class = module.uvicorn.Server

    def listener():
        sock = launcher.reserve_port(0)
        state['listeners'].append(sock)
        return sock

    def mcp_factory(*, directory):
        transport = OwnedStdio(command=shutil.which('node'),
            args=[str(ROOT / 'webhost/tests/fixtures/panel-server.mjs'), '--slow'],
            directory=directory, env={'CORELDRAW_MCP_RUNS': str(directory / 'runs')})
        mcp = CorelMcp(transport=transport, poll_interval=0.01)
        state['mcps'].append(mcp)
        return mcp

    def server_factory(config):
        server = server_class(config)
        # Uvicorn signal ownership belongs to the process, not this pytest task.
        from contextlib import nullcontext
        server.capture_signals = nullcontext
        state['servers'].append(server)
        return server

    def open_browser(url):
        state['urls'].append(url)
        return True

    monkeypatch.setattr(module, 'reserve_port', listener)
    monkeypatch.setattr(module, 'CorelMcp', mcp_factory)
    monkeypatch.setattr(module.uvicorn, 'Server', server_factory)
    monkeypatch.setattr(module.webbrowser, 'open', open_browser)
    return module, launcher, state


@pytest.mark.parametrize('no_browser', [False, True])
async def test_launcher_http_start_duplicate_stop_drain_and_history(tmp_path, launcher_harness, no_browser):
    module, launcher, state = launcher_harness
    directory = tmp_path / 'runtime with spaces'
    task = asyncio.create_task(module.serve(directory, no_browser=no_browser))
    try:
        await until(lambda: task.done() or (state['servers'] and state['servers'][0].started))
        if task.done():
            task.result()
        server = state['servers'][0]
        marker = (directory / 'mcp-owner.json').read_bytes()
        host_marker = (directory / 'runtime.json').read_bytes()

        if no_browser:
            assert state['urls'] == []
        else:
            await until(lambda: state['urls'])
        # A duplicate -NoBrowser does not mint a token or create another owner.
        before = len(state['urls'])
        await module.serve(directory, no_browser=True)
        assert len(state['urls']) == before
        # A normal duplicate uses the real Windows event to open the same host.
        await module.serve(directory)
        await until(lambda: len(state['urls']) == before + 1)
        assert len(state['mcps']) == len(state['servers']) == len(state['listeners']) == 1
        assert (directory / 'mcp-owner.json').read_bytes() == marker
        assert (directory / 'runtime.json').read_bytes() == host_marker

        port = state['listeners'][0].getsockname()[1]
        headers = {'Host': '127.0.0.1:8878', 'Origin': 'http://127.0.0.1:8878'}
        async with httpx.AsyncClient(base_url=f'http://127.0.0.1:{port}', headers=headers,
                                     trust_env=False) as client:
            assert (await client.get('/api/v1/health')).status_code == 401
            token = parse_qs(urlsplit(state['urls'][-1]).fragment)['bootstrap'][0]
            response = await client.post('/api/v1/access/bootstrap', headers={'X-Corel-Bootstrap': token})
            assert response.status_code == 200
            client.headers['X-Corel-CSRF'] = response.json()['csrf_token']
            response = await client.post('/api/v1/actions', json=command().model_dump(mode='json'))
            assert response.status_code == 202
            run_id = response.json()['id']
            async with asyncio.timeout(10):
                while True:
                    snapshot = (await client.get(f'/api/v1/runs/{run_id}')).json()
                    if snapshot['job_id']:
                        break
                    await asyncio.sleep(0.01)
            launcher.signal_existing(directory, 'stop')
            await until(lambda: server.should_exit)
            # Shutdown begins while the owned slow operation is still running.
            assert not task.done()
            assert (directory / 'runtime.json').exists()
            assert (directory / 'mcp-owner.json').exists()
        await asyncio.wait_for(asyncio.shield(task), 10)
        assert state['mcps'][0].transport.returncode == 0
        assert not (directory / 'runtime.json').exists()
        assert not (directory / 'mcp-owner.json').exists()
        assert state['listeners'][0].fileno() == -1

        store = Store(directory)
        await store.open()
        try:
            snapshot = await store.get_snapshot(run_id)
            assert snapshot.status == 'stopped' and snapshot.stop_requested
            operations = await store.get_operations(run_id)
            assert len(operations) == 1
            result = operations[0]['result']
            assert result['outcome'] == 'succeeded'
            assert result['cleanup'] == {'completed': True, 'errors': []}
            assert await store.recover() == []
        finally:
            await store.aclose()
        calls = [json.loads(line) for line in (directory / 'runs/calls.jsonl').read_text().splitlines()]
        assert [call['tool'] for call in calls] == ['corel_status']
    finally:
        if not task.done():
            await until(lambda: task.done() or state['servers'])
            if state['servers']:
                state['servers'][0].should_exit = True
        await asyncio.wait_for(asyncio.shield(task), 10)


@pytest.mark.parametrize('after_initialize', [False, True])
async def test_launcher_mcp_startup_failure_releases_only_its_resources(tmp_path, launcher_harness, monkeypatch,
                                                                     after_initialize):
    module, _, state = launcher_harness
    directory = tmp_path / 'private runtime'
    original_start = CorelMcp.start

    async def failing_start(mcp):
        if after_initialize:
            await original_start(mcp)
        raise AgentError('STARTUP_FIXTURE_FAILED', 'Фиктивный отказ после initialize.', 503)

    monkeypatch.setattr(CorelMcp, 'start', failing_start)
    with pytest.raises(AgentError) as caught:
        await module.serve(directory, no_browser=True)
    assert caught.value.code == 'STARTUP_FIXTURE_FAILED'
    assert state['urls'] == state['servers'] == []
    assert state['mcps'][0].transport.returncode == (0 if after_initialize else None)
    assert not (directory / 'runtime.json').exists()
    assert not (directory / 'mcp-owner.json').exists()
    assert state['listeners'][0].fileno() == -1


async def test_launcher_after_dead_host_recovers_dispatch_without_mcp_replay(tmp_path, launcher_harness):
    from test_recovery import operation

    module, launcher, state = launcher_harness
    directory = tmp_path / 'crashed runtime'
    module.ensure_private(directory)
    store = Store(directory)
    await store.open()
    try:
        run = (await store.accept_run(command(), None)).snapshot
        op = operation(run)
        await store.record_operation(op)
        await store.mark_dispatching(op.operation_id)
    finally:
        await store.aclose()

    # A child exits naturally without release, leaving an actual dead identity.
    child = await asyncio.create_subprocess_exec(sys.executable, '-c',
        'import sys; from pathlib import Path; from corel_agent.launcher import HostOwner; '
        'from corel_agent.owned_stdio import OwnerMarker; '
        'HostOwner(Path(sys.argv[1])).reserve(); OwnerMarker(Path(sys.argv[1]) / "mcp-owner.json").reserve()',
        str(directory), cwd=str(ROOT / 'webhost'))
    assert await child.wait() == 0
    assert (directory / 'runtime.json').exists()
    mcp_marker = (directory / 'mcp-owner.json').read_bytes()
    task = asyncio.create_task(module.serve(directory, no_browser=True))
    try:
        await until(lambda: task.done() or (state['servers'] and state['servers'][0].started))
        if task.done():
            task.result()
        assert len(state['mcps']) == 1
        assert state['mcps'][0].transport.process is None
        assert (directory / 'mcp-owner.json').read_bytes() == mcp_marker
        await module.serve(directory)
        await until(lambda: state['urls'])
        port = state['listeners'][0].getsockname()[1]
        async with httpx.AsyncClient(base_url=f'http://127.0.0.1:{port}', trust_env=False,
                headers={'Host': '127.0.0.1:8878', 'Origin': 'http://127.0.0.1:8878'}) as client:
            token = parse_qs(urlsplit(state['urls'][0]).fragment)['bootstrap'][0]
            response = await client.post('/api/v1/access/bootstrap', headers={'X-Corel-Bootstrap': token})
            assert response.status_code == 200
            client.headers['X-Corel-CSRF'] = response.json()['csrf_token']
            snapshot = (await client.get(f'/api/v1/runs/{run.id}')).json()
            assert snapshot['status'] == 'uncertain' and snapshot['safety_barrier']
            assert snapshot['job_id'] is None
            assert (await client.post('/api/v1/actions', json=command().model_dump())).status_code == 503
            operations = (await client.get(f'/api/v1/runs/{run.id}/operations')).json()['items']
            assert len(operations) == 1 and operations[0]['id'] == op.operation_id
        assert not (directory / 'runs').exists()
    finally:
        if state['servers']:
            launcher.signal_existing(directory, 'stop')
        await asyncio.wait_for(asyncio.shield(task), 10)
    assert not (directory / 'runtime.json').exists()
    assert (directory / 'mcp-owner.json').read_bytes() == mcp_marker


async def test_fixed_port_launcher_serves_compiled_ui_and_real_browser_without_com(tmp_path, launcher_harness, monkeypatch):
    module, launcher, state = launcher_harness
    # Restore the production reservation, not an ephemeral listener or Host override.
    monkeypatch.setattr(module, 'reserve_port', launcher.reserve_port)
    directory = tmp_path / 'production port fixture'
    task = asyncio.create_task(module.serve(directory))
    primary = None
    try:
        await until(lambda: task.done() or (state['servers'] and state['servers'][0].started))
        if task.done():
            try:
                task.result()
            except AgentError as error:
                if error.code == 'PORT_BUSY':
                    assert state['mcps'] == state['servers'] == []
                    pytest.skip('Порт 8878 занят; существующий сервис не затрагивается.')
                raise
        await until(lambda: state['urls'])
        async with httpx.AsyncClient(base_url='http://127.0.0.1:8878', trust_env=False) as client:
            response = await client.get('/')
            assert response.status_code == 200
            assert 'text/html' in response.headers['content-type']
            assert response.headers['x-content-type-options'] == 'nosniff'
            assert 'no-store' in response.headers['cache-control']
            assert "frame-ancestors 'none'" in response.headers['content-security-policy']
            assert (await client.get('/api/v1/health')).status_code == 401
            assert (await client.get('/api/v1/health', headers={'Host': '127.0.0.1:18880'})).status_code == 403

        driver = await asyncio.create_subprocess_exec(shutil.which('node'),
            str(ROOT / 'webhost/tests/launcher_browser.mjs'),
            env={**os.environ, 'COREL_TEST_BOOTSTRAP_URL': state['urls'][0]},
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        try:
            stdout, stderr = await asyncio.wait_for(driver.communicate(), 45)
        except TimeoutError:
            # Only this test-owned browser driver, never the host/MCP executor.
            driver.terminate()
            await driver.communicate()
            assert driver.returncode is not None
            raise
        assert driver.returncode == 0, stderr.decode(errors='replace')
        assert json.loads(stdout) == {'bootstrap_cleared': True, 'history_restored': True, 'mobile_no_overflow': True}
        assert len(state['mcps']) == len(state['servers']) == 1
        calls = [json.loads(line) for line in (directory / 'runs/calls.jsonl').read_text().splitlines()]
        assert [call['tool'] for call in calls] == ['corel_status']
    except BaseException as error:
        primary = error
        raise
    finally:
        if state['servers'] and not task.done():
            launcher.signal_existing(directory, 'stop')
        await finish_browser_host(task, primary=primary)
    assert state['mcps'][0].transport.returncode == 0
    assert not (directory / 'runtime.json').exists()
    assert not (directory / 'mcp-owner.json').exists()
