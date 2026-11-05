import { useRef, useState } from 'react';

export default function SettingsDialog({ save }: { save: (model: string, baseUrl: string, key: string, consent: boolean) => Promise<void> }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [consent, setConsent] = useState(false);
  const [model, setModel] = useState(''); const [baseUrl, setBaseUrl] = useState('http://127.0.0.1:1234/v1'); const [key, setKey] = useState('');
  function close() { setKey(''); setError(''); dialog.current?.close(); }
  return <><button onClick={() => { setError(''); dialog.current?.showModal(); }}>Настройки модели</button><dialog ref={dialog} aria-labelledby="settings-title" onCancel={event => { event.preventDefault(); if (!saving) close(); }}><h2 id="settings-title">Профиль модели</h2><form onSubmit={async event => { event.preventDefault(); setSaving(true); setError(''); try { await save(model, baseUrl, key, consent); close(); } catch (error) { setError(error instanceof Error ? error.message : 'Профиль не сохранён.'); } finally { setSaving(false); } }}>
    <label>Модель<input value={model} required disabled={saving} onChange={event => setModel(event.target.value)} /></label><label>API URL<input value={baseUrl} required disabled={saving} onChange={event => setBaseUrl(event.target.value)} /></label><label>API key<input type="password" value={key} disabled={saving} autoComplete="off" onChange={event => setKey(event.target.value)} /></label>
    <label><input type="checkbox" checked={consent} onChange={event => setConsent(event.target.checked)} />Разрешить передачу текстового контекста и кода этой модели. Превью не передаются.</label>
    {error ? <p role="alert" className="error">{error}</p> : null}
    <div className="dialog-actions"><button type="button" disabled={saving} onClick={close}>Отмена</button><button className="primary" disabled={saving}>Сохранить в Credential Manager</button></div></form></dialog></>;
}
