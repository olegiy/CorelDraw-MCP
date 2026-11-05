import type { components } from '../api.generated';

export default function RunPanel({ operations }: { operations: components['schemas']['OperationView'][] }) {
  return <div>{operations.map(operation => <details key={operation.id} className="operation-card">
    <summary>{operation.prepared.tool} · {operation.result?.outcome ?? operation.state}</summary>
    <p>Job: {operation.job_id ?? 'ещё не получен'}</p>
    <p>Applied: {String(operation.result?.applied ?? 'неизвестно')} · Cleanup: {String(operation.result?.cleanup?.completed ?? 'нет данных')}</p>
    <pre>{JSON.stringify(operation.prepared.arguments, null, 2)}</pre>
    <a href={`/api/v1/operations/${encodeURIComponent(operation.id)}/report`} target="_blank" rel="noreferrer">Локальный отчёт</a>
  </details>)}</div>;
}
