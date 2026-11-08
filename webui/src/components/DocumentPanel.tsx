import type { Documents } from '../api';

export default function DocumentPanel({ documents, documentId, busy, setDocumentId, refresh, action }: {
  documents: Documents; documentId: string; busy: boolean; setDocumentId: (id: string) => void; refresh: () => void;
  action: (tool: string, args?: Record<string, unknown>) => void;
}) {
  return <section className="panel document-panel"><h2>Документ</h2><button disabled={busy} onClick={refresh}>Обновить документы</button>
    <label>Целевой документ<select aria-label="Целевой документ" value={documentId} disabled={busy || documents.stale} onChange={event => setDocumentId(event.target.value)}>
      <option value="">Выберите документ</option>{(documents.items ?? []).map(doc => <option key={doc.document_id} value={doc.document_id}>{doc.name}</option>)}
    </select></label><small>{documents.stale ? 'Список устарел' : `Кэш: ${documents.at ?? 'нет данных'}`}</small>
    <button disabled={busy || !documentId} onClick={() => action('corel_inspect_document')}>Инспекция</button><button disabled={busy || !documentId} onClick={() => action('corel_save_document')}>Сохранить выбранный CDR</button>
    <button disabled={busy || !documentId} onClick={() => action('corel_close_document')}>Закрыть документ</button>
    <button disabled={busy || !documentId} onClick={() => action('corel_preview', { target: 'page' })}>Превью страницы</button>
    <button disabled={busy || !documentId} onClick={() => action('corel_preview', { target: 'selection' })}>Превью выделения</button>
  </section>;
}
