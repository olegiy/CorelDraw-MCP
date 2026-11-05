import { useEffect, useEffectEvent, useRef } from 'react';
import { api, terminal } from './api';
import type { components } from './api.generated';
import { applyRunEvent } from './runEvents';

export type StreamUpdate = components['schemas']['MessageDeltaEvent'] | components['schemas']['MessageEvent'] | components['schemas']['ToolProposedEvent'] | components['schemas']['ConfirmationEvent'] | components['schemas']['ToolStartEvent'] | components['schemas']['JobProgressEvent'] | components['schemas']['ToolResultEvent'] | components['schemas']['ImageEvent'] | components['schemas']['StatusEvent'] | components['schemas']['WarningEvent'] | components['schemas']['ErrorEvent'];

export function useRunStream(run: components['schemas']['RunSnapshot'] | null, onEvent: (event: StreamUpdate) => void, onRecovery: (snapshot: components['schemas']['RunSnapshot']) => void, onError: (message: string) => void, onState?: (state: components['schemas']['EventState']) => void) {
  const cursor = useRef({ id: '', seq: 0 });
  const latestEvent = useEffectEvent(onEvent);
  const recover = useEffectEvent(onRecovery);
  const fail = useEffectEvent(onError);
  const stateChanged = useEffectEvent((state: components['schemas']['EventState']) => onState?.(state));
  useEffect(() => {
    if (!run) return;
    cursor.current = { id: run.id, seq: run.last_seq };
    let closed = false;
    let source: EventSource;
    let recovering = false;
    const reconnect = async () => {
      if (closed || recovering) return;
      recovering = true;
      source.close();
      try {
        const snapshot = await api<components['schemas']['RunSnapshot']>(`/runs/${encodeURIComponent(run.id)}`);
        if (closed) return;
        cursor.current = { id: snapshot.id, seq: snapshot.last_seq };
        recover(snapshot);
        if (!terminal(snapshot.status)) connect(snapshot.last_seq);
      } catch { if (!closed) fail('Не удалось восстановить состояние Run из журнала.'); }
      finally { recovering = false; }
    };
    const read = (raw: string) => {
      try {
        const event = JSON.parse(raw) as StreamUpdate;
        const reduced = applyRunEvent({ id: cursor.current.id, last_seq: cursor.current.seq }, event);
        if (reduced.kind === 'ignore' || reduced.kind === 'duplicate') return;
        if (reduced.kind === 'recover') {
          void reconnect();
          return;
        }
        cursor.current.seq = event.seq;
        latestEvent(event);
        stateChanged(event.state);
        if (event.state.status === 'completed' || event.state.status === 'failed' || event.state.status === 'stopped' || event.state.status === 'uncertain' || event.state.status === 'halted') source.close();
      } catch { fail('Поток событий повреждён. Состояние доступно в истории.'); }
    };
    function connect(after: number) {
      source = new EventSource(`/api/v1/runs/${encodeURIComponent(run!.id)}/events?after=${after}`);
      source.addEventListener('update', event => { if (!closed) read((event as MessageEvent).data); });
      source.addEventListener('reset', () => { void reconnect(); });
      source.onerror = () => { if (!closed) fail('Соединение браузера потеряно. Агент продолжает работу в фоне.'); };
    }
    connect(run.last_seq);
    return () => { closed = true; source.close(); };
  }, [run?.id]);
}
