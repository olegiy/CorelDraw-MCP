"""Strict legacy decoding for stored evidence only, never for incoming commands."""

from copy import deepcopy

from pydantic import Field, TypeAdapter

from .contracts import (
    DTO,
    EVENT_ADAPTER,
    Digest,
    Id,
    OperationContext,
    PreparedOperation,
    ProfileSnapshot,
    RunCommand,
    RunSnapshot,
    Workspace,
)


class _Root(DTO):
    id: Id
    path: str = Field(min_length=1, max_length=1024)


class _Workspace(DTO):
    output_roots: list[_Root] = Field(max_length=8)


class _File(DTO):
    file_ref: Id
    display_name: str
    path: str
    fingerprint: str


class _Confirmation(DTO):
    confirmation_id: Id
    operation_digest: Digest
    operation: PreparedOperation
    reasons: tuple[str, ...]


def _removed_bool(data, key):
    if key in data and type(data.pop(key)) is not bool:
        raise ValueError(f'Invalid legacy {key}')


def profile(value):
    data = deepcopy(value)
    for key in ('context_approved', 'vision', 'send_preview'):
        if key in data and type(data.pop(key)) is not bool:
            raise ValueError('Invalid legacy profile')
    return ProfileSnapshot.model_validate(data)


def _roots(value):
    saved = _Workspace.model_validate(value)
    if len({root.id for root in saved.output_roots}) != len(saved.output_roots):
        raise ValueError('Duplicate legacy root')
    return saved.output_roots


def workspace(value):
    if 'output_roots' in value:
        roots = _roots(value)
        return Workspace(output_directory=roots[0].path if len(roots) == 1 else None)
    return Workspace.model_validate(value)


def _destination(data, saved_workspace):
    if 'output_root_id' in data:
        if 'output_directory' in data:
            raise ValueError('Conflicting destination fields')
        root_id = data.pop('output_root_id')
        if root_id is not None:
            TypeAdapter(Id).validate_python(root_id)
        roots = _roots(saved_workspace) if 'output_roots' in saved_workspace else []
        data['output_directory'] = next((root.path for root in roots if root.id == root_id), None)
    return data


def command(value, saved_workspace):
    data = _destination(deepcopy(value), saved_workspace)
    _removed_bool(data, 'read_only')
    return RunCommand.model_validate(data)


def snapshot(value, saved_workspace):
    data = _destination(deepcopy(value), saved_workspace)
    _removed_bool(data, 'read_only')
    pending = data.pop('pending_confirmation', None)
    if pending is not None:
        _Confirmation.model_validate(pending)
    if data.get('status') == 'awaiting_confirmation':
        data['status'] = 'interrupted'
    if data.get('profile') is not None:
        data['profile'] = profile(data['profile'])
    return RunSnapshot.model_validate(data)


def context(value, saved_workspace):
    data = deepcopy(value)
    _removed_bool(data, 'read_only')
    if 'allow_new_document' in data:
        if 'new_document' in data:
            raise ValueError('Conflicting document intent')
        data['new_document'] = data.pop('allow_new_document')
    if 'files' in data:
        TypeAdapter(tuple[_File, ...]).validate_python(data.pop('files'))
    if 'output_root' in data:
        if 'output_directory' in data:
            raise ValueError('Conflicting context destination')
        root = data.pop('output_root')
        data['output_directory'] = _Root.model_validate(root).path if root is not None else None
    data = _destination(data, saved_workspace)
    if data.get('profile') is not None:
        data['profile'] = profile(data['profile'])
    return OperationContext.model_validate(data)


def event(value):
    data = deepcopy(value)
    if 'state' in data:
        _removed_bool(data['state'], 'read_only')
    if data.get('type') == 'confirmation':
        _Confirmation.model_validate(data['data'])
        data['type'] = 'warning'
        data['data'] = {'code': 'LEGACY_CONFIRMATION',
                        'message': 'Историческое подтверждение не возобновляется; старый Run не отправляет новые операции.'}
    elif data.get('type') == 'status' and data['data'].get('status') == 'awaiting_confirmation':
        if set(data['data']) != {'status'}:
            raise ValueError('Invalid legacy status event')
        data['type'] = 'warning'
        data['data'] = {'code': 'LEGACY_CONFIRMATION', 'message': 'Историческое ожидание подтверждения завершено.'}
    return EVENT_ADAPTER.validate_python(data)
