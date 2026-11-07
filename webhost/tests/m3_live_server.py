"""Opt-in real launcher; private stdout is consumed only by the M3 coordinator."""
import asyncio
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from corel_agent import __main__ as launcher  # noqa: E402
from corel_agent.corel_mcp import CorelMcp  # noqa: E402


class ObservedMcp(CorelMcp):
    async def start(self):
        await super().start()
        print(json.dumps({'native_identity': self.transport.identity.model_dump()}), flush=True)

    async def call_job(self, operation, callback):
        row = {'operation_id': operation.operation_id, 'tool': operation.tool,
               'document_id': operation.document_id}
        with (self.transport.marker.path.parent / 'native-journal.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(row) + '\n')
        return await super().call_job(operation, callback)


def bootstrap(url):
    print(json.dumps({'bootstrap': url}), flush=True)
    return True


if __name__ == '__main__':
    if os.environ.get('CORELDRAW_AGENT_M3_LIVE') != '1' or sys.stdout.isatty():
        raise SystemExit('M3 coordinator and explicit live opt-in required.')
    directory = Path(sys.argv[1]).resolve()
    if not directory.name.startswith('m3-http-'):
        raise SystemExit('A dedicated M3 data root is required.')
    launcher.CorelMcp = ObservedMcp
    launcher.webbrowser.open = bootstrap
    asyncio.run(launcher.serve(directory))
