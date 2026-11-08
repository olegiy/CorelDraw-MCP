"""Idle test-only host/MCP pair that deliberately ignores its stop event."""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from corel_agent.launcher import ControlEvents, HostOwner  # noqa: E402
from corel_agent.owned_stdio import OwnerMarker, process_identity  # noqa: E402

directory = Path(sys.argv[1])
host = HostOwner(directory)
host.reserve()
events = ControlEvents(directory)
child = subprocess.Popen([shutil.which('node'), '-e',
    'process.stdin.resume(); process.stdin.on("end", () => process.exit(0)); setInterval(() => {}, 1000)'],
    stdin=subprocess.PIPE)
marker = OwnerMarker(directory / 'mcp-owner.json')
marker.reserve()
marker.bind(process_identity(child.pid))
print(json.dumps({'host': host.identity.model_dump(), 'child': process_identity(child.pid).model_dump()}), flush=True)
try:
    while True:
        if '--honor-stop' in sys.argv and events.take_stop():
            child.stdin.close()
            child.wait(timeout=5)
            marker.release(process_identity(child.pid))
            host.release()
            break
        time.sleep(0.01)
finally:
    if child.poll() is None:
        child.terminate()
    child.wait()
    events.close()
