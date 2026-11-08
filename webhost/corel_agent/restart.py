"""Explicit restart escalation, restricted to proven idle, identity-pinned owners."""

import ctypes
import json
import os
import shutil
import sqlite3
import sys
import time
from contextlib import ExitStack
from ctypes import wintypes
from pathlib import Path
from uuid import UUID

from . import persisted
from .assets import assert_no_links
from .contracts import TERMINAL_STATUSES, AgentError, OperationResult, ProcessIdentity
from .corel_mcp import JOB_ID
from .launcher import reserve_port, signal_existing, startup_lock
from .observability import TechnicalLog
from .owned_stdio import identity_alive, release_marker
from .storage_acl import ensure_private
from .store import SCHEMA_VERSION


def _uncertain():
    return AgentError('RESTART_OWNER_UNCERTAIN',
        'Владелец или его привязка изменены либо не проверены; завершение запрещено.', 503)


def _read_marker(path):
    assert_no_links(path)
    if not path.exists():
        return None
    if not path.is_file() or not 0 < path.stat().st_size <= 65536:
        raise _uncertain()
    raw = path.read_bytes()
    data = json.loads(raw)
    keys = {'phase', 'token', 'process'}
    if path.name == 'mcp-owner.json':
        keys.add('owner')
    if set(data) != keys or data.get('phase') != 'running' or str(UUID(data['token'])) != data['token']:
        raise _uncertain()
    return raw, data, ProcessIdentity.model_validate(data['process'])


def _check_markers(markers):
    for path, marker in markers:
        assert_no_links(path)
        if not path.exists() or path.read_bytes() != marker[0]:
            raise _uncertain()


def _parent_pid(pid):
    class Entry(ctypes.Structure):
        _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD),
            ('th32ProcessID', wintypes.DWORD), ('th32DefaultHeapID', ctypes.c_size_t),
            ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD),
            ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', wintypes.LONG),
            ('dwFlags', wintypes.DWORD), ('szExeFile', wintypes.WCHAR * 260)]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise _uncertain()
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(Entry)
        found = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while found:
            if entry.th32ProcessID == pid:
                return entry.th32ParentProcessID
            found = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    raise _uncertain()


def _pin_process(identity, images, parent=None):
    import win32api
    import win32event
    import win32process
    alive = identity_alive(identity)
    if alive is False:
        return None
    if alive is not True or identity.pid == os.getpid():
        raise _uncertain()
    handle = win32api.OpenProcess(0x1001 | win32event.SYNCHRONIZE, False, identity.pid)
    try:
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                     wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not kernel.QueryFullProcessImageNameW(int(handle), 0, buffer, ctypes.byref(size)):
            raise _uncertain()
        image = os.path.normcase(str(Path(buffer.value).resolve()))
        expected = {os.path.normcase(str(Path(value).resolve())) for value in images if value}
        if (win32process.GetProcessTimes(handle)['CreationTime'].isoformat() != identity.creation_time
                or image not in expected or (parent is not None and _parent_pid(identity.pid) != parent)):
            raise _uncertain()
        return handle
    except BaseException:
        handle.Close()
        raise


def _assert_idle(db, directory, child):
    blocked = AgentError('RESTART_NOT_IDLE',
        'Есть незавершённые или непроверенные операции; принудительный restart запрещён.', 409)
    if (db.execute('PRAGMA user_version').fetchone()[0] != SCHEMA_VERSION
            or db.execute('PRAGMA quick_check').fetchone()[0] != 'ok'):
        raise blocked
    workspace = db.execute("SELECT value FROM meta WHERE key='workspace'").fetchone()
    for snapshot, unfinished in db.execute('SELECT snapshot,unfinished FROM runs'):
        value = persisted.snapshot(json.loads(snapshot), json.loads(workspace[0]) if workspace else {})
        if unfinished or value.status not in TERMINAL_STATUSES or value.safety_barrier:
            raise blocked
    barrier = db.execute("SELECT value FROM meta WHERE key='safety_barrier'").fetchone()
    if barrier and json.loads(barrier[0]) is not None:
        raise blocked
    for state, value in db.execute('SELECT state,result FROM operations'):
        if state != 'terminal' or not value:
            raise blocked
        result = OperationResult.model_validate_json(value)
        if (result.outcome not in {'succeeded', 'failed'} or not result.report.get('finished_at')
                or not result.cleanup or result.cleanup.get('completed') is not True):
            raise blocked
    emergency = directory / 'emergency'
    assert_no_links(emergency)
    if emergency.exists() and any(emergency.iterdir()):
        raise blocked
    if child is None:
        return
    local = os.environ.get('LOCALAPPDATA')
    if not local:
        raise blocked
    reports = Path(local) / 'CorelDrawMcp/runs'
    assert_no_links(reports)
    if not reports.is_dir():
        raise blocked
    # Also catch Jobs submitted but not successfully persisted into the host ledger.
    for index, path in enumerate(reports.iterdir()):
        if index >= 100000:
            raise blocked
        assert_no_links(path)
        if not path.is_dir() or not JOB_ID.fullmatch(path.name):
            continue
        report_path = path / 'report.json'
        assert_no_links(report_path)
        if not report_path.is_file() or not 0 < report_path.stat().st_size <= 16 * 1024**2:
            raise blocked
        report = json.loads(report_path.read_text(encoding='utf-8'))
        if report.get('owner_pid') != child.pid:
            continue
        if (report.get('job_id') != path.name or report.get('status') not in {'succeeded', 'failed'}
                or not report.get('finished_at') or report.get('cleanup', {}).get('completed') is not True):
            raise blocked


def stop_for_restart(directory, *, timeout=15):
    """Graceful stop first; escalation never kills CorelDRAW, workers, or a process tree."""
    import win32api
    import win32event
    directory = Path(directory)
    host_path, child_path = directory / 'runtime.json', directory / 'mcp-owner.json'
    if not host_path.exists() and not child_path.exists():
        return
    with startup_lock(directory), ExitStack() as stack:
        try:
            ensure_private(directory)
            host, child = _read_marker(host_path), _read_marker(child_path)
            if host is None or (child and child[1].get('owner') != host[2].model_dump()):
                raise _uncertain()
            if identity_alive(host[2]) is True:
                signal_existing(directory, 'stop')
            deadline = time.monotonic() + timeout
            while identity_alive(host[2]) is True and time.monotonic() < deadline:
                time.sleep(min(0.1, max(0, deadline - time.monotonic())))
            if not host_path.exists() and not child_path.exists() and identity_alive(host[2]) is False:
                return
            # Reserving the closed listener prevents another process from taking the port
            # during escalation. No termination is permitted against a listening host.
            listener = reserve_port()
            stack.callback(listener.close)
            markers = [(path, marker) for path, marker in ((host_path, host), (child_path, child))
                       if marker is not None and path.exists()]
            for path, marker in ((host_path, host), (child_path, child)):
                if marker and not path.exists() and identity_alive(marker[2]) is not False:
                    raise _uncertain()
            handles = []
            for marker, images, parent in ((host, [sys.executable, sys._base_executable], None),
                                           (child, [shutil.which('node')], host[2].pid)):
                if marker:
                    handle = _pin_process(marker[2], images, parent)
                    if handle is not None:
                        stack.callback(handle.Close)
                        handles.append(handle)
            if handles:
                path = directory / 'history.sqlite3'
                assert_no_links(path)
                db = sqlite3.connect(path.as_uri() + '?mode=rw', uri=True, isolation_level=None, timeout=1)
                stack.callback(db.close)
                # Block new admissions/dispatch commits until both pinned owners exit.
                db.execute('BEGIN IMMEDIATE')
                _assert_idle(db, directory, child[2] if child else None)
            _check_markers(markers)
            log = TechnicalLog(directory / 'logs')
            stack.callback(log.close)
            log.write('restart_recovery', code='VERIFIED_IDLE_OWNER' if handles else 'EXITED_OWNER')
            for handle in handles:
                _check_markers(markers)
                if win32event.WaitForSingleObject(handle, 0) == win32event.WAIT_TIMEOUT:
                    try:
                        win32api.TerminateProcess(handle, 0)
                    except Exception:
                        # Closing the host's pipe can let MCP exit between wait and terminate.
                        if win32event.WaitForSingleObject(handle, 5000) != win32event.WAIT_OBJECT_0:
                            raise _uncertain() from None
                if win32event.WaitForSingleObject(handle, 5000) != win32event.WAIT_OBJECT_0:
                    raise _uncertain()
            for path, marker in markers:
                if identity_alive(marker[2]) is not False:
                    raise _uncertain()
                if path.exists():
                    if path.read_bytes() != marker[0]:
                        raise _uncertain()
                    release_marker(path, marker[0])
            log.write('restart_recovery', code='OWNER_RELEASED')
        except AgentError:
            raise
        except Exception:
            raise _uncertain() from None
