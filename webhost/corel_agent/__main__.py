"""Explicit local startup only; import does not open files, windows or MCP."""

import argparse
import asyncio
import os
import sys
import webbrowser
from contextlib import asynccontextmanager
from pathlib import Path

import uvicorn
from fastapi.staticfiles import StaticFiles

from .access import Access
from .app import create_app
from .assets import Assets
from .contracts import AgentError
from .corel_mcp import CorelMcp
from .launcher import (
    ControlEvents,
    HostOwner,
    preflight,
    release_dead_host,
    reserve_port,
    signal_existing,
    verify_existing,
)
from .observability import TechnicalLog
from .runtime import Runtime
from .settings import Settings
from .storage_acl import ensure_private
from .store import Store


async def prepare_runtime(store, mcp, directory):
    recovery = await store.recover()
    await store.put_meta("recovery_pending", recovery)
    if not recovery and not (directory / "mcp-owner.json").exists():
        await mcp.start()
        await store.put_meta("recovery_only", False)
    return Runtime(store, mcp, Assets(directory, store), settings=Settings(directory))


async def cleanup_resources(runtime, mcp, store, log, events, owner, listener):
    async def cleanup():
        error = None
        drained = False
        try:
            if runtime or mcp:
                await (runtime.aclose() if runtime else mcp.aclose())
            drained = True
        except BaseException as primary:
            error = primary
        for resource, method in ((store, 'aclose'), (log, 'close'), (events, 'close'),
                                 (owner if drained and owner and owner.identity else None, 'release'), (listener, 'close')):
            if resource is None:
                continue
            try:
                result = getattr(resource, method)()
                if method == 'aclose':
                    await result
            except BaseException as failure:
                if error is None:
                    error = failure
        if error is not None:
            raise error
    task = asyncio.create_task(cleanup(), name='launcher-resource-cleanup')
    cancelled = False
    while not task.done():
        try:
            await asyncio.shield(task)
        except asyncio.CancelledError:
            cancelled = True
    task.result()
    if cancelled:
        raise asyncio.CancelledError


async def run_controls(server, events, access, log, *, no_browser=False):
    opened = no_browser
    while not server.should_exit:
        if server.started and (not opened or events.take_open()):
            opened = True
            token = access.issue_bootstrap()
            try:
                success = await asyncio.to_thread(webbrowser.open, f'http://127.0.0.1:8878/#bootstrap={token}')
            except Exception:
                success = False
            if not success:
                log.write('browser_unavailable', code='BROWSER_UNAVAILABLE')
        if events.take_stop():
            server.should_exit = True
            return
        await asyncio.sleep(0.2)


async def serve(directory, *, no_browser=False):
    root = Path(__file__).resolve().parents[2]
    preflight(directory)
    ensure_private(directory)
    if (directory / "runtime.json").exists() and not release_dead_host(directory):
        verify_existing(directory)
        if not no_browser:
            signal_existing(directory, "open")
        return
    listener = reserve_port()
    owner = store = mcp = events = log = runtime = None
    try:
        owner = HostOwner(directory)
        store = Store(directory)
        mcp = CorelMcp(directory=directory)
        owner.reserve()
        events = ControlEvents(directory)
        log = TechnicalLog(directory / "logs")
        await store.open()
        runtime = await prepare_runtime(store, mcp, directory)
        runtime.technical_log = log
        if await store.get_meta("recovery_pending"):
            # Recovery-only host remains read-only until an explicit verified reconcile.
            await store.put_meta("recovery_only", True)
        access = Access()
        @asynccontextmanager
        async def lifespan(app):
            try:
                yield
            finally:
                await runtime.aclose()
        app = create_app(runtime=runtime, store=store, assets=runtime.assets, access=access, lifespan=lifespan)
        build = root / "webui/dist"
        if not (build / "index.html").is_file():
            raise AgentError("UI_BUILD_MISSING", "Сначала соберите webui через отдельный install script.", 503)
        app.mount("/", StaticFiles(directory=build, html=True), name="webui")
        server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=8878,
            access_log=False, log_config=None, timeout_graceful_shutdown=None, proxy_headers=False))
        task = asyncio.create_task(run_controls(server, events, access, log, no_browser=no_browser))
        log.write("host_started", code="READY")
        try:
            await server.serve(sockets=[listener])
        finally:
            server.should_exit = True
            await task
    finally:
        await cleanup_resources(runtime, mcp, store, log, events, owner, listener)


def main():
    parser = argparse.ArgumentParser(description="Локальная панель агента CorelDRAW")
    parser.add_argument("--no-browser", action="store_true")
    parser.add_argument("--stop", action="store_true")
    args = parser.parse_args()
    try:
        base = os.environ.get("LOCALAPPDATA")
        if not base:
            raise AgentError("DATA_DIRECTORY_UNAVAILABLE", "LOCALAPPDATA не определён.", 503)
        directory = Path(base) / "CorelDrawAgent"
        if args.stop:
            preflight(directory)
            signal_existing(directory, "stop")
        else:
            asyncio.run(serve(directory, no_browser=args.no_browser))
    except AgentError as error:
        # Only fixed sanitized codes/messages, never tokens or raw library exceptions.
        print(f"{error.code}: {error.message}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
