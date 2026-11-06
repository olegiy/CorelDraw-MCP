"""Windows singleton and port ownership. Never signal a process by name or port."""

import ctypes
import hashlib
import json
import os
import socket
import sys
from contextlib import contextmanager
from pathlib import Path

from .assets import assert_no_links
from .contracts import AgentError, ProcessIdentity, new_id
from .owned_stdio import identity_alive, process_identity, release_marker


def is_elevated():
    return sys.platform == "win32" and bool(ctypes.windll.shell32.IsUserAnAdmin())


def preflight(directory):
    if sys.platform != "win32" or sys.version_info[:2] != (3, 12):
        raise AgentError("PLATFORM_UNSUPPORTED", "Нужны Windows и Python 3.12.", 503)
    if is_elevated():
        raise AgentError("ELEVATION_DENIED", "Запуск панели с правами администратора запрещён.", 403)
    assert_no_links(Path(directory))


def reserve_port(port=8878):
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if sys.platform == "win32":
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind(("127.0.0.1", port))
        listener.listen(128)
        listener.setblocking(False)
        return listener
    except OSError:
        listener.close()
        raise AgentError("PORT_BUSY", "Loopback порт занят; другой MCP не запускается.", 503) from None


class HostOwner:
    def __init__(self, directory):
        self.path = Path(directory) / "runtime.json"
        self.token = new_id()
        self.identity = None

    def reserve(self):
        assert_no_links(self.path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        identity = process_identity(os.getpid())
        try:
            with self.path.open("x", encoding="utf-8") as stream:
                json.dump({"phase": "running", "token": self.token,
                           "process": identity.model_dump()}, stream)
                stream.flush()
                os.fsync(stream.fileno())
        except FileExistsError:
            raise AgentError("HOST_OWNER_UNCERTAIN", "Существующий владелец host требует проверки; повторный запуск запрещён.", 503) from None
        self.identity = identity

    def release(self):
        assert_no_links(self.path)
        try:
            original = self.path.read_bytes()
            data = json.loads(original)
            valid = (self.identity is not None and data.get("token") == self.token
                     and data.get("process") == self.identity.model_dump()
                     and process_identity(os.getpid()) == self.identity)
        except (OSError, ValueError):
            valid = False
        if not valid:
            raise AgentError("HOST_OWNER_UNCERTAIN", "Нельзя освободить непроверенное singleton-владение.", 503)
        try:
            release_marker(self.path, original)
        except AgentError:
            raise AgentError("HOST_OWNER_UNCERTAIN", "Нельзя освободить непроверенное singleton-владение.", 503) from None


def event_names(directory):
    digest = hashlib.sha256(str(Path(directory).resolve()).lower().encode("utf-8")).hexdigest()[:24]
    return {kind: f"Local\\CorelDrawAgent_{digest}_{kind}" for kind in ("open", "stop")}


@contextmanager
def startup_lock(directory):
    import pywintypes
    import win32event
    handle = None
    acquired = False
    try:
        name = event_names(directory)['open'].removesuffix('_open') + '_startup'
        handle = win32event.CreateMutex(user_security(), False, name)
        status = win32event.WaitForSingleObject(handle, 30000)
        acquired = status in (win32event.WAIT_OBJECT_0, win32event.WAIT_ABANDONED)
        if status != win32event.WAIT_OBJECT_0:
            raise AgentError('STARTUP_OWNER_UNCERTAIN', 'Запуск/удаление заняты или прежнее владение требует проверки.', 503)
        yield
    except pywintypes.error:
        raise AgentError('STARTUP_OWNER_UNCERTAIN', 'Не удалось подтвердить владение запуском/удалением.', 503) from None
    finally:
        if handle is not None:
            try:
                if acquired:
                    win32event.ReleaseMutex(handle)
            finally:
                handle.Close()


def user_security():
    import pywintypes
    import win32api
    import win32con
    import win32security
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32con.TOKEN_QUERY)
    try:
        sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
        dacl = win32security.ACL()
        dacl.AddAccessAllowedAce(win32security.ACL_REVISION, win32con.GENERIC_ALL, sid)
        descriptor = win32security.SECURITY_DESCRIPTOR()
        descriptor.SetSecurityDescriptorDacl(True, dacl, False)
        attributes = pywintypes.SECURITY_ATTRIBUTES()
        attributes.SECURITY_DESCRIPTOR = descriptor
        return attributes
    finally:
        token.Close()


class ControlEvents:
    def __init__(self, directory):
        import win32api
        import win32event
        self.handles = {}
        try:
            for kind, name in event_names(directory).items():
                handle = win32event.CreateEvent(user_security(), False, False, name)
                if win32api.GetLastError() == 183:
                    handle.Close()
                    raise AgentError("CONTROL_OWNER_UNCERTAIN", "Windows-событие уже принадлежит другому владельцу.", 503)
                self.handles[kind] = handle
        except BaseException:
            self.close()
            raise

    def request_open(self):
        import win32event
        win32event.SetEvent(self.handles["open"])

    def request_stop(self):
        import win32event
        win32event.SetEvent(self.handles["stop"])

    def take_open(self):
        return self._take("open")

    def take_stop(self):
        return self._take("stop")

    def _take(self, kind):
        import win32event
        return win32event.WaitForSingleObject(self.handles[kind], 0) == win32event.WAIT_OBJECT_0

    def close(self):
        for handle in self.handles.values():
            handle.Close()
        self.handles.clear()


def signal_existing(directory, kind):
    import pywintypes
    import win32event
    if kind not in {"open", "stop"}:
        raise AgentError("CONTROL_INVALID", "Неизвестное действие launcher.", 422)
    path = Path(directory) / "runtime.json"
    try:
        assert_no_links(path)
        data = json.loads(path.read_text(encoding="utf-8"))
        identity = ProcessIdentity.model_validate(data["process"])
        if data.get("phase") != "running" or identity_alive(identity) is not True:
            raise ValueError
        handle = win32event.OpenEvent(0x0002, False, event_names(directory)[kind])
        try:
            win32event.SetEvent(handle)
        finally:
            handle.Close()
    except (OSError, ValueError, KeyError, pywintypes.error):
        raise AgentError("HOST_OWNER_UNCERTAIN", "Живой владелец и его control event не подтверждены.", 503) from None


def verify_existing(directory):
    path = Path(directory) / "runtime.json"
    try:
        assert_no_links(path)
        data = json.loads(path.read_text(encoding="utf-8"))
        identity = ProcessIdentity.model_validate(data["process"])
        if data.get("phase") != "running" or identity_alive(identity) is not True:
            raise ValueError
    except (ValueError, OSError, KeyError):
        raise AgentError("HOST_OWNER_UNCERTAIN", "Существующий владелец не подтверждён; запуск запрещён.", 503) from None


def release_dead_host(directory):
    path = Path(directory) / "runtime.json"
    try:
        assert_no_links(path)
        original = path.read_bytes()
        data = json.loads(original)
        identity = ProcessIdentity.model_validate(data["process"])
        if data.get("phase") != "running" or identity_alive(identity) is not False:
            return False
        if path.read_bytes() != original or identity_alive(identity) is not False:
            return False
        release_marker(path, original)
        return True
    except (OSError, ValueError, KeyError, AgentError):
        return False
