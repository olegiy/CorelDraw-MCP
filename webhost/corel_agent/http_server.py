"""HTTP listener lifecycle owned by this host; MCP draining remains separate."""

import asyncio
import sys

import uvicorn


class LocalHttpServer(uvicorn.Server):
    async def _wait_tasks_to_complete(self):
        if sys.platform != 'win32' or sys.version_info[:2] != (3, 12):
            return await super()._wait_tasks_to_complete()
        while self.server_state.connections or self.server_state.tasks:
            await asyncio.sleep(.1)
        if any(listener.is_serving() for listener in self.servers):
            raise RuntimeError('HTTP_LISTENER_NOT_CLOSED')
        # CPython 3.12 Proactor can skip Server._detach after WinError 10054.
        # The listener is closed and all actual Uvicorn requests have drained;
        # waiting for that stale transport counter would prevent MCP cleanup.
