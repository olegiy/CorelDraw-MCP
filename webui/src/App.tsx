import { useEffect, useRef, useState } from 'react';
import { api, terminal } from './api';
import type { Documents, Preview, Run } from './api';
import PreviewPanel from './components/PreviewPanel';
import Chat from './components/Chat';
import DocumentPanel from './components/DocumentPanel';
import SettingsDialog from './components/SettingsDialog';
import type { components } from './api.generated';
import ApprovalDialog from './components/ApprovalDialog';
import RunPanel from './components/RunPanel';
import { useRunStream } from './useRunStream';
import FileActions from './components/FileActions';
import MessageList from './components/MessageList';

export default function App() {
  const [documents, setDocuments] = useState<Documents>({ items: [], at: null, stale: true });
  const [documentId, setDocumentId] = useState('');
  const [run, setRun] = useState<Run | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false); const [readOnly, setReadOnly] = useState(true);
  const [profileId, setProfileId] = useState<string | null>(null);
  const [rootId, setRootId] = useState<string | null>(null);
  const [newDocument, setNewDocument] = useState(false);
  const [operations, setOperations] = useState<components['schemas']['OperationView'][]>([]);
  const [approval, setApproval] = useState<components['schemas']['ConfirmationRequest'] | null>(null);
  const [runs, setRuns] = useState<Run[]>([]);
  const [messages, setMessages] = useState<components['schemas']['MessageView'][]>([]);
  const pendingRequest = useRef<string | null>(null);
  const [conversation, setConversation] = useState<string>(() => crypto.randomUUID());
  const watermark = useRef({ id: '', seq: 0 });
  const runId = run?.id;
  const busy = pending || (!!run && !terminal(run.status));

  useEffect(() => { void api<Documents>('/documents').then(setDocuments).catch(error => setError(error.message)); }, []);
  useEffect(() => { void api<components['schemas']['ProfilePage']>('/profiles').then(page => setProfileId(page.items[0]?.id ?? null)).catch(error => setError(error.message)); }, []);
  useEffect(() => { void api<components['schemas']['RunPage']>('/runs').then(page => {
    setRuns(page.items); const active = page.items.find(item => !terminal(item.status));
    if (active) { watermark.current = { id: active.id, seq: active.last_seq }; setConversation(active.conversation_id); setRun(active); setReadOnly(active.read_only !== false); setDocumentId(active.document_id ?? ''); setApproval(active.pending_confirmation ?? null); }
  }).catch(error => setError(error.message)); }, []);
  useEffect(() => {
    if (!run) return;
    let active = true;
    setConversation(run.conversation_id);
    setRuns(current => [run, ...current.filter(item => item.id !== run.id)]);
    void (async () => { let offset = 0; const all = []; for (;;) { const page = await api<components['schemas']['MessagePage']>(`/conversations/${run.conversation_id}/messages?offset=${offset}&limit=100`); all.push(...page.items); if (page.next_offset == null || page.next_offset <= offset) break; offset = page.next_offset; } if (active) setMessages(all); })().catch(error => { if (active) setError(error.message); });
    void (async () => { let offset = 0; const all = []; for (;;) { const page = await api<components['schemas']['OperationPage']>(`/runs/${run.id}/operations?offset=${offset}&limit=100`); all.push(...page.items); if (page.next_offset == null || page.next_offset <= offset) break; offset = page.next_offset; } if (active) setOperations(all); })().catch(error => { if (active) setError(error.message); });
    return () => { active = false; };
  }, [runId, run?.status]);
  useEffect(() => { setPreview(null); }, [runId]);
  useEffect(() => {
    if (!run || run.status !== 'stopping') return;
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    async function reconcileStop() {
      try {
        const snapshot = await api<Run>(`/runs/${encodeURIComponent(run!.id)}`);
        if (!active) return;
        setRun(current => current?.id === snapshot.id && snapshot.last_seq >= current.last_seq ? snapshot : current);
        if (!terminal(snapshot.status)) timer = setTimeout(reconcileStop, 500);
      } catch (error) { if (active) setError(error instanceof Error ? error.message : 'Snapshot недоступен.'); }
    }
    void reconcileStop();
    return () => { active = false; clearTimeout(timer); };
  }, [runId, run?.status]);
  useRunStream(run, event => { watermark.current.seq = event.seq; setRun(current => current ? { ...current, status: event.state.status, last_seq: event.seq } : current); if (event.type === 'image') setPreview(event.data); if (event.type === 'confirmation') setApproval(event.data); if (event.type === 'status' && event.state.status !== 'awaiting_confirmation') setApproval(null); if (event.type === 'status' && terminal(event.state.status)) void api<Documents>('/documents').then(setDocuments).catch(error => setError(error.message)); if (['tool_start', 'tool_result', 'confirmation'].includes(event.type)) void api<components['schemas']['OperationPage']>(`/runs/${event.run_id}/operations`).then(page => setOperations(page.items)).catch(error => setError(error.message)); }, snapshot => { watermark.current = { id: snapshot.id, seq: snapshot.last_seq }; setRun(snapshot); setReadOnly(snapshot.read_only !== false); setApproval(snapshot.pending_confirmation ?? null); void (async () => { let after = 0; for (;;) { const page = await api<components['schemas']['EventPage']>(`/runs/${snapshot.id}/event-log?after=${after}&limit=500`); const latest = [...page.items].reverse().find(item => item.type === 'image'); if (latest?.type === 'image') setPreview(latest.data); if (page.next_after == null) break; if (page.next_after <= after) throw new Error('Некорректная пагинация журнала.'); after = page.next_after; } })().catch(error => setError(error.message)); }, setError, state => { setRun(current => current ? { ...current, status: state.status, document_id: state.document_id, job_id: state.job_id, monitoring_state: state.monitoring_state, safety_barrier: state.safety_barrier, stop_requested: state.stop_requested } : current); });

  async function action(tool: string, arguments_: Record<string, unknown> = {}) {
    setPending(true); setError(''); setRun(null);
    const clientRequestId = crypto.randomUUID();
    pendingRequest.current = clientRequestId;
    try {
      const accepted = await api<Run>('/actions', { method: 'POST', body: JSON.stringify({
        client_request_id: clientRequestId, conversation_id: conversation, kind: 'manual', tool,
        document_id: ['corel_status', 'corel_create_document', 'corel_open_document'].includes(tool) ? null : documentId, arguments: arguments_, read_only: readOnly, output_root_id: rootId,
      }) });
      watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted);
    } catch (error) {
      try {
        const accepted = await api<Run>(`/requests/${clientRequestId}`);
        watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted);
      } catch { setError(error instanceof Error ? error.message : 'Не удалось подтвердить принятие команды.'); }
    } finally { setPending(false); pendingRequest.current = null; }
  }

  async function agent(message: string) {
    setPending(true); setError(''); setRun(null);
    const requestId = crypto.randomUUID(); pendingRequest.current = requestId;
    try { const accepted = await api<Run>('/runs', { method: 'POST', body: JSON.stringify({ client_request_id: requestId, conversation_id: conversation, kind: 'agent', profile_id: profileId, document_id: documentId || null, message, arguments: {}, read_only: readOnly, output_root_id: rootId, new_document: newDocument && !readOnly && !documentId }) }); watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted); }
    catch (error) { try { const accepted = await api<Run>(`/requests/${requestId}`); watermark.current = { id: accepted.id, seq: accepted.last_seq }; setRun(accepted); } catch { setError(error instanceof Error ? error.message : 'Не удалось подтвердить принятие команды. Не отправляйте её повторно с новым ID.'); } } finally { setPending(false); pendingRequest.current = null; }
  }

  async function saveProfile(model: string, baseUrl: string, key: string, consent: boolean, vision: boolean, sendPreview: boolean) {
    try {
      const id = profileId ?? crypto.randomUUID();
      const saved = await api<components['schemas']['ProfileSnapshot']>(`/profiles/${id}`, { method: 'PUT', body: JSON.stringify({ profile: { id, name: model, model, base_url: baseUrl, local: ['127.0.0.1', 'localhost', '[::1]'].includes(new URL(baseUrl).hostname), context_approved: consent, vision, send_preview: sendPreview }, ...(key ? { api_key: key } : {}) }) });
      setProfileId(saved.id);
    } catch (error) { throw error instanceof Error ? error : new Error('Профиль не сохранён.'); }
  }

  return <div className="shell">
    <header className="app-header"><div><h1>CorelDRAW Agent</h1><small>Локальная панель · {readOnly ? 'Только чтение' : 'Обычная запись разрешена; VBA требует подтверждения'}</small></div>
      <div className="header-actions"><SettingsDialog save={saveProfile} />{pendingRequest.current || (run && !terminal(run.status)) ? <button className="danger" onClick={() => { const request = pendingRequest.current; const promise = request ? api<Run | null>(`/requests/${request}/stop`, { method: 'POST' }) : api<Run>(`/runs/${run!.id}/stop`, { method: 'POST' }); void promise.then(snapshot => { if (snapshot) { watermark.current = { id: snapshot.id, seq: snapshot.last_seq }; setRun(snapshot); } }).catch(error => setError(error.message)); }}>Стоп</button> : null}</div>
    </header>
    <main className="workspace">
      <div><DocumentPanel documents={documents} documentId={documentId} busy={busy} readOnly={readOnly} setReadOnly={setReadOnly} setDocumentId={setDocumentId} refresh={() => void action('corel_status')} action={(tool, args) => void action(tool, args)} />
      <FileActions disabled={busy || readOnly} hasDocument={!!documentId} rootId={rootId} setRootId={setRootId} action={(tool, args) => void action(tool, args)} fail={setError} />
      <section className="panel"><h2>История</h2>{runs.map(item => <button key={item.id} disabled={busy && item.id !== run?.id} onClick={() => { watermark.current = { id: item.id, seq: item.last_seq }; setRun(item); setApproval(item.pending_confirmation ?? null); }}>{item.kind} · {item.status}</button>)}</section></div>
      <section className="panel"><h2>Операции</h2><p role="status">{run ? `Состояние: ${run.status}` : 'Подключение к CorelDRAW выполняется только по явному запросу.'}</p>
        {error ? <p role="alert" className="error">{error}</p> : null}
        {run ? <div data-testid="safety-state"><p>Цель Run: {run.document_id ?? 'не выбрана'} · Job: {run.job_id ?? 'нет'} · Мониторинг: {run.monitoring_state ?? 'healthy'}</p>{run.safety_barrier ? <p role="alert" className="error">Барьер безопасности: {run.safety_barrier.reason}. Новые изменения запрещены до проверки.</p> : null}{run.stop_requested ? <p>Stop запрошен; уже отправленный COM ожидает завершения.</p> : null}</div> : null}
        <p className="muted">Закрытие вкладки не останавливает отправленную операцию. «Стоп» запрещает следующие шаги, но не отменяет COM и не выполняет Undo.</p>
        <MessageList messages={messages} />
        <RunPanel operations={operations} /><PreviewPanel preview={preview} /><label><input type="checkbox" checked={newDocument && !readOnly && !documentId} disabled={busy || readOnly || !!documentId} onChange={event => setNewDocument(event.target.checked)} />Агент создаёт новый документ</label><Chat busy={busy} submit={agent} />
      </section>
    </main>
    <ApprovalDialog pending={approval} decide={approved => { if (!run || !approval) return; const saved = approval; setApproval(null); void api<Run>(`/runs/${run.id}/confirm`, { method: 'POST', body: JSON.stringify({ confirmation_id: saved.confirmation_id, operation_digest: saved.operation_digest, approved }) }).then(setRun).catch(error => setError(error.message)); }} />
  </div>;
}
