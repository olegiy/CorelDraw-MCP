import { useEffect, useState } from 'react';
import { api } from '../api';
import type { components } from '../api.generated';

export default function RecoveryPanel({ documentId, busy, newContext, refreshKey }: { documentId: string; busy: boolean; newContext: () => void; refreshKey?: string }) {
  const [barrier, setBarrier] = useState<components['schemas']['SafetyBarrier'] | null>(null);
  const [checked, setChecked] = useState(false); const [note, setNote] = useState('');
  const [error, setError] = useState(''); const [saving, setSaving] = useState(false);
  const [connected, setConnected] = useState(true); const [restartRequired, setRestartRequired] = useState(false);
  useEffect(() => { let active = true; void api<components['schemas']['Health']>('/health').then(health => { if (active) { setBarrier(health.safety_barrier ?? null); setConnected(health.mcp_connected !== false); } }).catch(error => { if (active) setError(error.message); }); return () => { active = false; }; }, [refreshKey]);
  if (!barrier && connected) return error ? <p role="alert">{error}</p> : null;
  async function release() {
    setSaving(true); setError('');
    try { await api('/recovery/review', { method: 'POST', body: JSON.stringify({ note, document_id: documentId, barrier_id: barrier!.id, release_for_new_context: true, manual_corel_checked: checked }) }); setBarrier(null); setChecked(false); setNote(''); newContext(); }
    catch (error) { setError(error instanceof Error ? error.message : 'Барьер не снят.'); }
    finally { setSaving(false); }
  }
  async function reconcile() {
    setSaving(true); setError('');
    try { await api('/recovery/reconcile', { method: 'POST', body: '{}' }); setRestartRequired(true); }
    catch (error) { setError(error instanceof Error ? error.message : 'Отчёты не сверены.'); }
    finally { setSaving(false); }
  }
  return <section className="panel" data-testid="global-barrier"><h2>Восстановление</h2><p role="alert">Барьер: {barrier?.reason ?? 'MCP не подключён'}. Изменения запрещены.</p>
    <p>Старый исход не меняется. При незавершённом Job или непроверенном владельце сервер откажет. Ручная проверка не является автоматической проверкой рисунка.</p>
    {!connected ? <><button disabled={busy || saving || restartRequired} onClick={() => void reconcile()}>Сверить прежние отчёты</button><p>{restartRequired ? 'Отчёты сверены. Штатно остановите host через Stop-CorelAgent, затем запустите Start-CorelAgent. После нового подключения обновите документы, выберите точный рисунок и рассмотрите барьер. Старый план не возобновляется.' : 'Для сверки прежний владелец должен быть доказанно завершён. Затем требуется штатный Stop/Start host, а не повтор старой команды.'}</p></> : null}
    <label>Комментарий рассмотрения<textarea value={note} disabled={saving} onChange={event => setNote(event.target.value)} maxLength={4000} /></label>
    <label><input type="checkbox" checked={checked} disabled={saving} onChange={event => setChecked(event.target.checked)} />Я вручную проверил CorelDRAW и исходную диагностику</label>
    {error ? <p role="alert" className="error">{error}</p> : null}
    <button disabled={busy || saving || !connected || !barrier || !checked || !note.trim() || !documentId} onClick={() => void release()}>Разрешить новый контекст</button>
  </section>;
}
