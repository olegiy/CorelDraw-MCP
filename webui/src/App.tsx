import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { api, terminal } from './api';
import type { Documents, Preview, Run } from './api';
import PreviewPanel from './components/PreviewPanel';

interface Update { schema_version: number; run_id: string; seq: number; type: string;
  data: Record<string, unknown>; state: { status: Run['status'] } }

export default function App() {
  const [documents, setDocuments] = useState<Documents>({ items: [], at: null, stale: true });
  const [documentId, setDocumentId] = useState('');
  const [run, setRun] = useState<Run | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  const [conversation] = useState(() => crypto.randomUUID());
  const watermark = useRef({ id: '', seq: 0 });
  const runId = run?.id;
  const busy = pending || (!!run && !terminal(run.status));

  useEffect(() => { void api<Documents>('/documents').then(setDocuments).catch(error => setError(error.message)); }, []);
  const onUpdate = useEffectEvent((event: Update) => {
    if (event.run_id !== watermark.current.id || event.seq <= watermark.current.seq) return false;
    if (event.schema_version !== 1 || !Number.isSafeInteger(event.seq) || event.seq !== watermark.current.seq + 1) {
      void api<Run>(`/runs/${encodeURIComponent(watermark.current.id)}`).then(snapshot => {
        watermark.current.seq = snapshot.last_seq; setRun(snapshot);
      }).catch(error => setError(error.message));
      return false;
    }
    watermark.current.seq = event.seq;
    setRun(current => current ? { ...current, status: event.state.status, last_seq: event.seq } : current);
    if (event.type === 'image') setPreview(event.data as unknown as Preview);
    if (event.type === 'status' && terminal(event.state.status)) {
      void api<Documents>('/documents').then(setDocuments).catch(error => setError(error.message));
      return true;
    }
    return false;
  });
  useEffect(() => {
    if (!runId) return;
    const source = new EventSource(`/api/v1/runs/${encodeURIComponent(runId)}/events?after=${watermark.current.seq}`);
    source.addEventListener('update', event => {
      try { if (onUpdate(JSON.parse((event as MessageEvent).data))) source.close(); }
      catch { setError('Поток событий повреждён. Состояние доступно в истории.'); }
    });
    source.onerror = () => setError('Соединение браузера потеряно. Агент продолжает работу в фоне.');
    return () => source.close();
  }, [runId]);

  async function action(tool: string, arguments_: Record<string, unknown> = {}) {
    setPending(true); setError(''); setRun(null);
    const clientRequestId = crypto.randomUUID();
    try {
      const accepted = await api<Run>('/actions', { method: 'POST', body: JSON.stringify({
        client_request_id: clientRequestId, conversation_id: conversation, kind: 'manual', tool,
        document_id: tool === 'corel_status' ? null : documentId, arguments: arguments_, read_only: true,
      }) });
      watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted);
    } catch (error) {
      try {
        const accepted = await api<Run>(`/requests/${clientRequestId}`);
        watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted);
      } catch { setError(error instanceof Error ? error.message : 'Не удалось подтвердить принятие команды.'); }
    } finally { setPending(false); }
  }

  return <div className="shell">
    <header className="app-header"><div><h1>CorelDRAW Agent</h1><small>Локальная панель · Только чтение</small></div>
      {run && !terminal(run.status) ? <button className="danger" onClick={() => { void api<Run>(`/runs/${run.id}/stop`, { method: 'POST' }).then(setRun).catch(error => setError(error.message)); }}>Стоп</button> : null}
    </header>
    <main className="workspace">
      <section className="panel document-panel"><h2>Документ</h2>
        <button disabled={busy} onClick={() => void action('corel_status')}>Обновить документы</button>
        <label>Целевой документ<select value={documentId} disabled={busy || documents.stale} onChange={event => setDocumentId(event.target.value)}>
          <option value="">Выберите документ</option>{(documents.items ?? []).map(doc => <option key={doc.document_id} value={doc.document_id}>{doc.name}</option>)}
        </select></label>
        <small>{documents.stale ? 'Список устарел' : `Кэш: ${documents.at ?? 'нет данных'}`}</small>
        <button disabled={busy || !documentId} onClick={() => void action('corel_inspect_document')}>Инспекция</button>
        <button disabled={busy || !documentId} onClick={() => void action('corel_preview', { target: 'page' })}>Превью страницы</button>
        <button disabled={busy || !documentId} onClick={() => void action('corel_preview', { target: 'selection' })}>Превью выделения</button>
      </section>
      <section className="panel"><h2>Операции</h2><p role="status">{run ? `Состояние: ${run.status}` : 'Подключение к CorelDRAW выполняется только по явному запросу.'}</p>
        {error ? <p role="alert" className="error">{error}</p> : null}
        <p className="muted">Закрытие вкладки не останавливает отправленную операцию. «Стоп» запрещает следующие шаги, но не отменяет COM и не выполняет Undo.</p>
        <PreviewPanel preview={preview} />
      </section>
    </main>
  </div>;
}
