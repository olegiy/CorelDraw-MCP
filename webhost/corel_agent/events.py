"""Bounded wakeup-only subscriptions. All event payloads come from durable SQLite."""

import asyncio
from contextlib import asynccontextmanager

from .contracts import TERMINAL_STATUSES


class EventHub:
    def __init__(self, queue_size=100):
        self.queue_size = queue_size
        self._subscribers = {}
        self.slow_subscribers = 0

    @property
    def subscriber_count(self):
        return sum(len(items) for items in self._subscribers.values())

    @asynccontextmanager
    async def subscribe(self, run_id):
        queue = asyncio.Queue(self.queue_size)
        self._subscribers.setdefault(run_id, set()).add(queue)
        try:
            yield queue
        finally:
            self._subscribers.get(run_id, set()).discard(queue)
            if not self._subscribers.get(run_id):
                self._subscribers.pop(run_id, None)

    def publish(self, run_id):
        for queue in tuple(self._subscribers.get(run_id, ())):
            try:
                queue.put_nowait(True)
            except asyncio.QueueFull:
                while not queue.empty():
                    queue.get_nowait()
                queue.put_nowait(False)
                self._subscribers[run_id].discard(queue)
                self.slow_subscribers += 1


async def run_stream(store, hub, run_id, after=0):
    async with hub.subscribe(run_id) as queue:
        while True:
            events = await store.get_events(run_id, after=after, limit=100)
            for event in events:
                after = event.seq
                yield {"event": "update", "id": str(event.seq), "data": event.model_dump_json()}
            if len(events) == 100:
                continue
            snapshot = await store.get_snapshot(run_id)
            if snapshot.status in TERMINAL_STATUSES and snapshot.last_seq <= after:
                return
            try:
                if await asyncio.wait_for(queue.get(), 2) is False:
                    yield {"event": "reset", "data": "{}"}
                    return
            except TimeoutError:
                yield {"event": "heartbeat", "data": "{}"}
