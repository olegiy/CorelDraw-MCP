import { useState } from 'react';

export default function Chat({ busy, submit }: { busy: boolean; submit: (message: string) => void }) {
  const [message, setMessage] = useState('');
  return <form className="chat-composer" onSubmit={event => { event.preventDefault(); if (message.trim()) { submit(message.trim()); setMessage(''); } }}>
    <label htmlFor="agent-message">Сообщение агенту</label>
    <textarea id="agent-message" value={message} disabled={busy} rows={3} maxLength={65536} placeholder="Опишите задачу для CorelDRAW"
      aria-describedby="agent-message-hint"
      onChange={event => setMessage(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); event.currentTarget.form?.requestSubmit(); } }} />
    <div className="composer-actions"><small id="agent-message-hint">Enter: отправить · Shift+Enter: новая строка</small><button type="submit" className="primary composer-submit" disabled={busy || !message.trim()}>Отправить</button></div>
  </form>;
}
