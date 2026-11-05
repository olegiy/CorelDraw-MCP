import asyncio

import pytest
from conftest import implementation


async def test_launcher_cleanup_repeated_cancellation_keeps_resources_until_drain():
    module = implementation('__main__')
    events = []
    gate = asyncio.Event()
    class Runtime:
        async def aclose(self):
            events.append('drain_started')
            await gate.wait()
            events.append('drained')
    class Store:
        async def aclose(self):
            events.append('store_closed')
    class Resource:
        identity = True
        def __init__(self, name):
            self.name = name
        def close(self):
            events.append(self.name)
        def release(self):
            events.append(self.name)
    task = asyncio.create_task(module.cleanup_resources(Runtime(), None, Store(),
        Resource('log_closed'), Resource('events_closed'), Resource('owner_released'), Resource('socket_closed')))
    await asyncio.sleep(0.01)
    task.cancel()
    await asyncio.sleep(0.01)
    task.cancel()
    await asyncio.sleep(0.01)
    assert not task.done() and events == ['drain_started']
    gate.set()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert events == ['drain_started', 'drained', 'store_closed', 'log_closed',
                      'events_closed', 'owner_released', 'socket_closed']


async def test_real_serve_releases_singleton_and_socket_after_cancelled_drain(tmp_path, monkeypatch):
    module = implementation('__main__')
    events = []
    gate, started = asyncio.Event(), asyncio.Event()
    class Owner:
        identity = None
        def __init__(self, directory):
            pass
        def reserve(self):
            self.identity = True
        def release(self):
            events.append('owner_released')
    class Store:
        def __init__(self, directory):
            pass
        async def open(self):
            pass
        async def get_meta(self, key):
            return []
        async def aclose(self):
            events.append('store_closed')
    class Resource:
        def __init__(self, *args, **kwargs):
            pass
        def close(self):
            events.append('closed')
        def take_open(self):
            return False
        def take_stop(self):
            return False
        def write(self, *args, **kwargs):
            pass
    class Runtime:
        assets = None
        async def aclose(self):
            events.append('drain_started')
            await gate.wait()
            events.append('drained')
    class Server:
        started = False
        should_exit = False
        def __init__(self, config):
            pass
        async def serve(self, sockets):
            started.set()
            await asyncio.Event().wait()
    async def prepare(*args):
        return Runtime()
    monkeypatch.setattr(module, 'preflight', lambda directory: None)
    monkeypatch.setattr(module, 'ensure_private', lambda directory: None)
    monkeypatch.setattr(module, 'reserve_port', lambda: Resource())
    monkeypatch.setattr(module, 'HostOwner', Owner)
    monkeypatch.setattr(module, 'Store', Store)
    monkeypatch.setattr(module, 'CorelMcp', Resource)
    monkeypatch.setattr(module, 'ControlEvents', Resource)
    monkeypatch.setattr(module, 'TechnicalLog', Resource)
    monkeypatch.setattr(module, 'prepare_runtime', prepare)
    monkeypatch.setattr(module.uvicorn, 'Server', Server)
    task = asyncio.create_task(module.serve(tmp_path, no_browser=True))
    await asyncio.wait_for(started.wait(), 5)
    task.cancel()
    await asyncio.sleep(0.25)
    task.cancel()
    await asyncio.sleep(0.02)
    assert 'owner_released' not in events
    gate.set()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert events.index('drained') < events.index('store_closed') < events.index('owner_released')
    assert events[-1] == 'closed'


@pytest.mark.parametrize('failure', ['drain', 'store', 'log', 'events', 'owner'])
async def test_cleanup_failure_preserves_primary_error_and_closes_safe_resources(failure):
    from corel_agent.contracts import AgentError
    module = implementation('__main__')
    events = []
    class Resource:
        identity = True
        def __init__(self, name):
            self.name = name
        async def aclose(self):
            self.close()
        def close(self):
            events.append(self.name)
            if self.name == failure:
                raise AgentError('PRIMARY_CLEANUP', 'fixed diagnostic')
        def release(self):
            self.close()
    with pytest.raises(AgentError) as caught:
        await module.cleanup_resources(Resource('drain'), None, Resource('store'), Resource('log'),
                                        Resource('events'), Resource('owner'), Resource('socket'))
    assert caught.value.code == 'PRIMARY_CLEANUP'
    assert 'store' in events and 'log' in events and 'events' in events and 'socket' in events
    assert ('owner' in events) is (failure != 'drain')


async def test_constructor_failure_closes_reserved_socket(tmp_path, monkeypatch):
    module = implementation('__main__')
    events = []
    class Listener:
        def close(self):
            events.append('socket_closed')
    monkeypatch.setattr(module, 'preflight', lambda directory: None)
    monkeypatch.setattr(module, 'ensure_private', lambda directory: None)
    monkeypatch.setattr(module, 'reserve_port', lambda: Listener())
    monkeypatch.setattr(module, 'Store', lambda directory: (_ for _ in ()).throw(OSError('constructor')))
    with pytest.raises(OSError):
        await module.serve(tmp_path, no_browser=True)
    assert events == ['socket_closed']


@pytest.mark.parametrize('outcome', ['exception', 'false'])
async def test_browser_failure_does_not_disable_stop_control(monkeypatch, outcome):
    module = implementation('__main__')
    calls = []
    class Server:
        started = True
        should_exit = False
    class Events:
        def take_open(self):
            return False
        def take_stop(self):
            calls.append('stop')
            return True
    class Access:
        def issue_bootstrap(self):
            return 'CANARY-bootstrap'
    class Log:
        def write(self, event, **fields):
            assert 'CANARY' not in str(fields)
            calls.append(fields.get('code'))
    def opening(url):
        if outcome == 'exception':
            raise OSError('CANARY-bootstrap')
        return False
    monkeypatch.setattr(module.webbrowser, 'open', opening)
    server = Server()
    await module.run_controls(server, Events(), Access(), Log(), no_browser=False)
    assert server.should_exit and calls == ['BROWSER_UNAVAILABLE', 'stop']
