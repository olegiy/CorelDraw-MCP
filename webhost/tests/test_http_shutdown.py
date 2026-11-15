import asyncio

import pytest
import uvicorn

from corel_agent.http_server import LocalHttpServer


async def app(scope, receive, send):
    pass


async def test_windows_listener_counter_leak_does_not_block_after_http_drain(monkeypatch):
    from corel_agent import http_server

    monkeypatch.setattr(http_server.sys, 'platform', 'win32')
    monkeypatch.setattr(http_server.sys, 'version_info', (3, 12, 10))
    server = LocalHttpServer(uvicorn.Config(app))
    listener = await asyncio.start_server(lambda reader, writer: writer.close(), '127.0.0.1', 0)
    # Reproduce CPython's skipped _detach after a reset in transport shutdown.
    listener._attach()
    listener.close()
    server.servers = [listener]
    try:
        await asyncio.wait_for(server._wait_tasks_to_complete(), 1)
        assert not listener.is_serving()
    finally:
        listener._detach()
        await listener.wait_closed()


async def test_http_drain_keeps_pending_requests_and_tasks_until_they_finish():
    server = LocalHttpServer(uvicorn.Config(app))
    server.servers = []
    connection = object()
    server.server_state.connections.add(connection)
    gate = asyncio.Event()
    request = asyncio.create_task(gate.wait())
    server.server_state.tasks.add(request)
    draining = asyncio.create_task(server._wait_tasks_to_complete())
    try:
        await asyncio.sleep(.15)
        assert not draining.done()
        server.server_state.connections.remove(connection)
        await asyncio.sleep(.15)
        assert not draining.done()
        gate.set()
        await request
        server.server_state.tasks.remove(request)
        await asyncio.wait_for(draining, 1)
    finally:
        gate.set()
        await request
        if not draining.done():
            draining.cancel()
            with pytest.raises(asyncio.CancelledError):
                await draining


async def test_windows_http_drain_refuses_a_listener_still_accepting_requests():
    server = LocalHttpServer(uvicorn.Config(app))
    listener = await asyncio.start_server(lambda reader, writer: writer.close(), '127.0.0.1', 0)
    server.servers = [listener]
    try:
        with pytest.raises(RuntimeError, match='HTTP_LISTENER_NOT_CLOSED'):
            await server._wait_tasks_to_complete()
    finally:
        listener.close()
        await listener.wait_closed()
