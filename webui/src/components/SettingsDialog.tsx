import { useEffect, useRef, useState } from 'react';
import type { components } from '../api.generated';

export default function SettingsDialog({ save, profile }: { profile?: components['schemas']['ProfileSnapshot'] | null; save: (model: string, baseUrl: string, key: string) => Promise<void> }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [model, setModel] = useState(''); const [baseUrl, setBaseUrl] = useState('http://127.0.0.1:1234/v1'); const [key, setKey] = useState('');
  function close() { setKey(''); setError(''); dialog.current?.close(); }
  useEffect(() => { setKey(''); if (profile) { setModel(profile.model ?? ''); setBaseUrl(profile.base_url ?? 'http://127.0.0.1:1234/v1'); } }, [profile]);
  return <><button className="settings-trigger" onClick={() => { setError(''); dialog.current?.showModal(); }}>Настройки модели</button><dialog ref={dialog} aria-labelledby="settings-title" onCancel={event => { event.preventDefault(); if (!saving) close(); }}><h2 id="settings-title">Профиль модели</h2><p>Контекст и проверенные PNG-превью передаются модели автоматически.</p><form onSubmit={async event => { event.preventDefault(); setSaving(true); setError(''); try { await save(model, baseUrl, key); close(); } catch (error) { setError(error instanceof Error ? error.message : 'Профиль не сохранён.'); } finally { setSaving(false); } }}>
    <label>Модель<input value={model} required disabled={saving} onChange={event => setModel(event.target.value)} /></label><label>API URL<input value={baseUrl} required disabled={saving} onChange={event => setBaseUrl(event.target.value)} /></label><label>API key<input type="password" value={key} disabled={saving} autoComplete="off" onChange={event => setKey(event.target.value)} /></label>
    {error ? <p role="alert" className="error">{error}</p> : null}
    <div className="dialog-actions"><button type="button" disabled={saving} onClick={close}>Отмена</button><button className="primary" disabled={saving}>Сохранить в Credential Manager</button></div></form></dialog></>;
}
