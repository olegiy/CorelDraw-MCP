"""Evidence for unloading a retained diagnostic GMS; never changes native reports."""

import json
import ntpath
import re
from datetime import UTC, datetime

from .contracts import OperationResult, PreparedOperation
from .policy import Policy
from .store import fingerprint


def timestamp(value):
    if not isinstance(value, str):
        raise ValueError('Missing timestamp')
    parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None:
        raise ValueError('Missing timezone')
    return parsed


def verified(row):
    """Require the same complete terminal binding used for native job admission."""
    operation = PreparedOperation.model_validate_json(row['prepared'])
    result = OperationResult.model_validate_json(row['result'])
    report = result.report
    if (row['state'] != 'terminal' or not row['job_id']
            or operation.operation_id != row['id'] or operation.run_id != row['run_id']
            or operation.policy_version not in {1, 2, 3}
            or operation.digest != Policy.operation_digest(operation)
            or result.operation_id != row['id'] or result.job_id != row['job_id']
            or result.outcome not in {'succeeded', 'failed'}
            or json.loads(row['report']) != report
            or report.get('job_id') != row['job_id'] or report.get('status') != result.outcome
            or report.get('request_id') != operation.corel_request_id
            or report.get('tool') != operation.tool or not report.get('finished_at')
            or not result.cleanup or result.cleanup.get('completed') is not True
            or result.cleanup.get('errors') != [] or report.get('cleanup') != result.cleanup):
        raise ValueError('Unconfirmed terminal report')
    return operation, result


def target(row):
    operation, result = verified(row)
    cleanup = result.cleanup
    name = cleanup.get('project_name')
    directory = result.report.get('run_directory')
    if (operation.tool not in {'corel_execute_vba', 'corel_build_vba'}
            or cleanup.get('project_retained') is not True
            or not isinstance(name, str) or not re.fullmatch(r'CorelMcp_[0-9a-f]{12}', name, re.I)
            or not isinstance(directory, str) or not ntpath.isabs(directory)
            or ntpath.basename(ntpath.normpath(directory)) != row['job_id']):
        raise ValueError('Not a retained diagnostic project')
    path = ntpath.normcase(ntpath.normpath(ntpath.join(directory, name + '.gms')))
    artifacts = result.report.get('artifacts', [])
    if not isinstance(artifacts, list) or not any(
            isinstance(item, str) and ntpath.normcase(ntpath.normpath(item)) == path
            for item in artifacts):
        raise ValueError('Missing diagnostic artifact binding')
    return name, path, result.report['finished_at']


def observation(row, *, owner_pid=None, fresh=False):
    operation, result = verified(row)
    report = result.report
    status = report.get('result')
    vba = status.get('vba') if isinstance(status, dict) else None
    inventory = vba.get('temporary_projects') if isinstance(vba, dict) else None
    if (operation.tool != 'corel_status' or result.outcome != 'succeeded'
            or result.cleanup.get('project_retained') is True
            or not isinstance(inventory, dict) or inventory.get('complete') is not True
            or inventory.get('idle') is not True
            or not isinstance(inventory.get('gms'), list) or len(inventory['gms']) > 10000
            or not isinstance(inventory.get('vbe_names'), list) or len(inventory['vbe_names']) > 10000
            or any(not isinstance(name, str) or not name for name in inventory['vbe_names'])
            or any(not isinstance(item, dict) or not isinstance(item.get('name'), str)
                   or not item['name'] or not isinstance(item.get('path'), str) or not item['path']
                   for item in inventory['gms'])):
        raise ValueError('Incomplete project inventory')
    started = timestamp(report.get('started_at'))
    finished = timestamp(report.get('finished_at'))
    if started > finished:
        raise ValueError('Invalid observation time')
    if fresh and (report.get('owner_pid') != owner_pid
                  or not 0 <= (datetime.now(UTC) - finished).total_seconds() <= 60):
        raise ValueError('Stale observation or changed owner')
    return inventory, started


def absence(retained, observed):
    name, path, finished_at = target(retained)
    inventory, started = observation(observed)
    finished = timestamp(finished_at)
    if finished > started:
        raise ValueError('Observation predates retained project')
    if (any(item['name'].casefold() == name.casefold()
            or ntpath.normcase(ntpath.normpath(item['path'])) == path
            or ntpath.basename(item['path']).casefold() == name.casefold() + '.gms'
            for item in inventory['gms'])
            or any(item.casefold() == name.casefold() for item in inventory['vbe_names'])):
        raise ValueError('Diagnostic project is still loaded')
    return name


def commitment(row):
    return fingerprint({key: json.loads(row[key]) if key in {'prepared', 'result', 'report'} else row[key]
                        for key in ('id', 'run_id', 'state', 'job_id', 'prepared', 'result', 'report')})
