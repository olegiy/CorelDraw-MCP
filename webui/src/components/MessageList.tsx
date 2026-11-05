import type { components } from '../api.generated';

export default function MessageList({ messages }: { messages: components['schemas']['MessageView'][] }) {
  return <div className="messages">{messages.map(message => {
    let artifact: { artifact_id?: string; display_name?: string } | null = null;
    try { const parsed = JSON.parse(message.text); if (parsed && typeof parsed.artifact_id === 'string') artifact = parsed; } catch { /* plain text */ }
    return <article key={message.id}><strong>{message.role}</strong><p>{artifact ? `Зарегистрирован результат: ${artifact.display_name ?? 'artifact'}` : message.text}</p>{artifact ? <a href={`/api/v1/artifacts/${encodeURIComponent(artifact.artifact_id!)}`}>Скачать {artifact.display_name ?? 'artifact'}</a> : null}</article>;
  })}</div>;
}
