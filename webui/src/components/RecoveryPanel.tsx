import { useEffect, useState } from 'react';
import { api } from '../api';
import type { components } from '../api.generated';

export default function RecoveryPanel({ busy, refreshKey }: { busy: boolean; refreshKey?: string }) {
  const [barrier, setBarrier] = useState<components['schemas']['SafetyBarrier'] | null>(null);
  const [error, setError] = useState(''); const [saving, setSaving] = useState(false);
  const [connected, setConnected] = useState(true); const [restartRequired, setRestartRequired] = useState(false);
  const [reason, setReason] = useState<string | null>(null);
  useEffect(() => {
    let active = true; let timer: ReturnType<typeof setTimeout> | undefined;
    async function refresh() {
      try {
        const health = await api<components['schemas']['Health']>('/health');
        if (!active) return;
        setBarrier(health.safety_barrier ?? null); setConnected(health.mcp_connected !== false);
        setReason(health.readiness_reason ?? null);
        if (!busy && health.readiness_reason === 'ACTIVE_TASKS') timer = setTimeout(() => void refresh(), 150);
      } catch (error) { if (active) setError(error instanceof Error ? error.message : 'Состояние исполнителя недоступно.'); }
    }
    void refresh();
    return () => { active = false; if (timer) clearTimeout(timer); };
  }, [refreshKey, busy]);
  if (!barrier && connected && !reason) return error ? <p role="alert">{error}</p> : null;
  async function reconcile() {
    setSaving(true); setError('');
    try { await api('/recovery/reconcile', { method: 'POST', body: '{}' }); setRestartRequired(true); }
    catch (error) { setError(error instanceof Error ? error.message : 'Отчёты не сверены.'); }
    finally { setSaving(false); }
  }
  return <section className="panel recovery-panel" role="alert" data-testid="global-barrier"><h2>Восстановление</h2><p>Исполнитель не готов: {reason ?? barrier?.reason ?? 'MCP не подключён'}.</p>
    <p>Допуск проверяется автоматически по terminal results, cleanup, владельцу MCP и свежему кэшу документов. Неопределённый исход и незавершённые эффекты блокируют новые изменения. Исход старого Run не меняется.</p>
    {reason === 'CLEANUP_NOT_PROVEN' ? <p>Если временный проект CorelMcp уже выгружен, нажмите «Обновить документы». Панель проверит его отсутствие в GMS и VBE и сохранит отдельное подтверждение. Загруженный проект, неполная проверка или ошибки восстановления сохраняют блокировку.</p> : null}
    {!connected ? <><button disabled={busy || saving || restartRequired} onClick={() => void reconcile()}>Сверить прежние отчёты</button><p>{restartRequired ? 'Отчёты сверены. Штатно остановите host через Stop-CorelAgent, затем запустите Start-CorelAgent и обновите документы. Readiness проверяется автоматически; старый план не возобновляется.' : 'Для сверки прежний владелец должен быть доказанно завершён. Затем требуется штатный Stop/Start host, а не повтор старой команды.'}</p></> : null}
    {error ? <p role="alert" className="error">{error}</p> : null}
  </section>;
}
