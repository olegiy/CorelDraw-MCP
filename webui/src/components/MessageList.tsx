import type { components } from '../api.generated';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

export default function MessageList({ messages }: { messages: components['schemas']['MessageView'][] }) {
  return <div className="messages">{messages.map(message => {
    let artifact: { artifact_id?: string; display_name?: string } | null = null;
    try { const parsed = JSON.parse(message.text); if (parsed && typeof parsed.artifact_id === 'string') artifact = parsed; } catch { /* plain text */ }
    return <article key={message.id} className={`message message-${message.role}`} data-role={message.role}><strong>{message.role}</strong>{artifact ? <p>Зарегистрирован результат: {artifact.display_name ?? 'artifact'}</p> : <Markdown skipHtml remarkPlugins={[remarkGfm]} disallowedElements={['img']} components={{ a: ({ children }) => <span>{children}</span> }}>{message.text}</Markdown>}{artifact ? <a href={`/api/v1/artifacts/${encodeURIComponent(artifact.artifact_id!)}`}>Скачать {artifact.display_name ?? 'artifact'}</a> : null}</article>;
  })}</div>;
}
