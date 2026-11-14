"""Owned Windows stdio, using official ClientSession but never its force-kill context.

The lifecycle task alone enters/exits the SDK cancel scope. Closing a caller or
subscriber cannot close this transport. stdin EOF is followed by natural exit,
without termination deadlines, kill signals, shell commands, or Job Objects.
"""

import asyncio
import json
import ntpath
import os
import sys
from datetime import timedelta
from importlib.metadata import version
from pathlib import Path

import anyio
from mcp import ClientSession, types
from mcp.shared.message import SessionMessage

from .contracts import AgentError, ProcessIdentity, new_id

SAFE_ENV = frozenset({
    "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PATHEXT",
    "PROCESSOR_ARCHITECTURE", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP",
    "USERNAME", "USERPROFILE", "WINDIR",
})


class LifecycleError(AgentError):
    pass


def sdk_version():
    return version("mcp")


def process_identity(pid: int) -> ProcessIdentity:
    if sys.platform != "win32":
        raise LifecycleError("PLATFORM_UNSUPPORTED", "Нужна Windows с проверяемым process identity.", 503)
    import win32api
    import win32process

    handle = win32api.OpenProcess(0x1000, False, pid)
    try:
        created = win32process.GetProcessTimes(handle)["CreationTime"].isoformat()
        return ProcessIdentity(pid=pid, creation_time=created)
    finally:
        handle.Close()


def identity_alive(identity: ProcessIdentity) -> bool | None:
    try:
        if process_identity(identity.pid) != identity:
            return None
        import win32api
        import win32event
        import win32process

        handle = win32api.OpenProcess(0x1000 | win32event.SYNCHRONIZE, False, identity.pid)
        try:
            created = win32process.GetProcessTimes(handle)['CreationTime'].isoformat()
            if created != identity.creation_time:
                return None
            # Windows can retain an exited process object while handles remain open.
            state = win32event.WaitForSingleObject(handle, 0)
            return False if state == win32event.WAIT_OBJECT_0 else True if state == win32event.WAIT_TIMEOUT else None
        finally:
            handle.Close()
    except Exception as error:
        # pywintypes.error is not an OSError; 87 means no such process ID.
        return False if getattr(error, "winerror", None) == 87 else None


def restricted_environment(extra=None):
    env = {key: value for key, value in os.environ.items() if key.upper() in SAFE_ENV}
    worker_keys = {'CORELDRAW_MCP_BACKEND', 'CORELDRAW_MCP_CSHARP_WORKER', 'DOTNET_ROOT_X64'}
    env.update({key: os.environ[key] for key in worker_keys if key in os.environ})
    for key, value in (extra or {}).items():
        if key not in {"CORELDRAW_MCP_RUNS"} | worker_keys:
            raise LifecycleError("CHILD_ENV_DENIED", "Недопустимая переменная окружения MCP.", 403)
        env[key] = value
    if env.get('CORELDRAW_MCP_BACKEND', 'csharp') != 'csharp':
        raise LifecycleError('CHILD_ENV_DENIED', 'Неподдерживаемый backend MCP.', 403)
    for key in ('CORELDRAW_MCP_CSHARP_WORKER', 'DOTNET_ROOT_X64'):
        if key in env and (not isinstance(env[key], str) or not ntpath.isabs(env[key])
                           or len(ntpath.splitdrive(env[key])[0]) != 2
                           or any(ord(char) < 32 for char in env[key])):
            raise LifecycleError('CHILD_ENV_DENIED', 'Для исполнителя нужен абсолютный локальный путь Windows.', 403)
    return env


def atomic_json(path: Path, data):
    temporary = path.with_name(f".{path.name}.{new_id()}.tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(data, stream, ensure_ascii=False, allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def release_marker(path: Path, original: bytes):
    import win32con
    import win32file

    from .assets import assert_no_links

    handle = None
    try:
        assert_no_links(path)
        handle = win32file.CreateFile(str(path), win32con.GENERIC_READ | win32con.DELETE,
            win32con.FILE_SHARE_READ, None, win32con.OPEN_EXISTING,
            win32file.FILE_FLAG_OPEN_REPARSE_POINT, None)
        _, current = win32file.ReadFile(handle, len(original) + 1)
        if current != original:
            raise LifecycleError('OWNERSHIP_UNCERTAIN', 'Маркер владения изменён; освобождение запрещено.', 503)
        # Deny replacement/write sharing and delete this exact open file, not its path.
        win32file.SetFileInformationByHandle(handle, win32file.FileDispositionInfo, True)
    except Exception:
        raise LifecycleError('OWNERSHIP_UNCERTAIN', 'Маркер владения не удалось безопасно освободить.', 503) from None
    finally:
        if handle is not None:
            handle.Close()


class OwnerMarker:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.token = new_id()

    def reserve(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        data = {"phase": "spawn_pending", "token": self.token,
                "owner": process_identity(os.getpid()).model_dump()}
        try:
            with self.path.open("x", encoding="utf-8") as stream:
                json.dump(data, stream)
                stream.flush()
                os.fsync(stream.fileno())
        except FileExistsError:
            raise LifecycleError("OWNERSHIP_UNCERTAIN", "Прежний владелец MCP не проверен; запуск запрещён.", 503) from None

    def _owned(self):
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
            if data["token"] == self.token:
                return data
        except (ValueError, KeyError, OSError):
            pass
        raise LifecycleError("OWNERSHIP_UNCERTAIN", "Маркер владения MCP изменён.", 503)

    def bind(self, identity: ProcessIdentity):
        data = self._owned()
        atomic_json(self.path, {**data, "phase": "running", "process": identity.model_dump()})

    def release(self, identity: ProcessIdentity):
        original = self.path.read_bytes()
        data = self._owned()
        if data.get("process") != identity.model_dump():
            raise LifecycleError("OWNERSHIP_UNCERTAIN", "Process identity не совпадает с владельцем.", 503)
        release_marker(self.path, original)


class OwnedStdio:
    def __init__(self, *, command: str, args: list[str], directory: Path, env=None, cwd=None):
        self.command, self.args = command, list(args)
        self.env, self.cwd = restricted_environment(env), cwd
        self.marker = OwnerMarker(Path(directory) / "mcp-owner.json")
        self.session: ClientSession | None = None
        self.process: asyncio.subprocess.Process | None = None
        self.identity: ProcessIdentity | None = None
        self._task: asyncio.Task | None = None
        self._close = asyncio.Event()
        self._ready: asyncio.Future | None = None
        self._no_child = False

    @property
    def returncode(self):
        return self.process.returncode if self.process else None

    async def verify_owner(self):
        def verify():
            from .assets import assert_no_links
            assert_no_links(self.marker.path)
            data = self.marker._owned()
            if (self.identity is None or self.process is None or self.returncode is not None
                    or data.get('phase') != 'running' or data.get('process') != self.identity.model_dump()
                    or identity_alive(self.identity) is not True):
                raise LifecycleError('OWNERSHIP_UNCERTAIN', 'Точный текущий владелец MCP не подтверждён.', 503)
        await asyncio.to_thread(verify)

    async def start(self):
        if self._task:
            raise LifecycleError("ALREADY_STARTED", "Transport уже запущен.")
        if sdk_version() != "1.30.0" or sys.version_info[:2] != (3, 12):
            raise LifecycleError("SDK_UNSUPPORTED", "Проверены только Python 3.12 и MCP SDK 1.30.0.", 503)
        await asyncio.to_thread(self.marker.reserve)
        self._ready = asyncio.get_running_loop().create_future()
        self._task = asyncio.create_task(self._run(), name="owned-corel-mcp")
        await asyncio.shield(self._ready)

    async def _run(self):
        reader = writer = None
        read_send, read_receive = anyio.create_memory_object_stream(0)
        write_send, write_receive = anyio.create_memory_object_stream(0)
        try:
            self.process = await asyncio.create_subprocess_exec(
                self.command, *self.args, env=self.env, cwd=self.cwd,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL, limit=16 * 1024 * 1024,
                creationflags=0x08000000 if sys.platform == "win32" else 0,
            )
            self.identity = await asyncio.to_thread(process_identity, self.process.pid)
            await asyncio.to_thread(self.marker.bind, self.identity)

            async def receive():
                try:
                    async with read_send:
                        while line := await self.process.stdout.readline():
                            try:
                                message = types.JSONRPCMessage.model_validate_json(line)
                            except Exception:
                                await read_send.send(ValueError("Некорректный MCP JSONRPC."))
                                break
                            await read_send.send(SessionMessage(message))
                except (anyio.ClosedResourceError, anyio.BrokenResourceError):
                    pass

            async def send():
                async with write_receive:
                    async for message in write_receive:
                        raw = message.message.model_dump_json(by_alias=True, exclude_none=True)
                        self.process.stdin.write((raw + "\n").encode("utf-8"))
                        await self.process.stdin.drain()

            reader = asyncio.create_task(receive())
            writer = asyncio.create_task(send())
            async with ClientSession(read_receive, write_send,
                                     read_timeout_seconds=timedelta(seconds=30)) as session:
                await session.initialize()
                self.session = session
                self._ready.set_result(None)
                await self._close.wait()
        except BaseException as error:
            if self.process is None and isinstance(error, OSError):
                self._no_child = True
            if self._ready and not self._ready.done():
                self._ready.set_exception(LifecycleError("MCP_START_FAILED", "Не удалось инициализировать owned MCP.", 503))
            if not isinstance(error, asyncio.CancelledError):
                self._close.set()
        finally:
            if writer:
                writer.cancel()
                await asyncio.gather(writer, return_exceptions=True)
            if self.process:
                self.process.stdin.close()
                # Natural process completion is deliberately unbounded, and cannot kill COM.
                await asyncio.shield(self.process.wait())
            if reader:
                reader.cancel()
                await asyncio.gather(reader, return_exceptions=True)
            for stream in (read_send, read_receive, write_send, write_receive):
                await stream.aclose()
            if self.identity and self.process.returncode is not None:
                await asyncio.to_thread(self.marker.release, self.identity)
            elif self._no_child:
                original = await asyncio.to_thread(self.marker.path.read_bytes)
                data = await asyncio.to_thread(self.marker._owned)
                if data.get('phase') == 'spawn_pending' and data.get('owner') == process_identity(os.getpid()).model_dump():
                    await asyncio.to_thread(release_marker, self.marker.path, original)
            self.session = None

    async def aclose(self):
        self._close.set()
        if self._task:
            cancelled = False
            while not self._task.done():
                try:
                    await asyncio.shield(self._task)
                except asyncio.CancelledError:
                    cancelled = True
            self._task.result()
            if cancelled:
                raise asyncio.CancelledError
