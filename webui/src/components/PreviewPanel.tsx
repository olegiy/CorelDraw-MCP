import type { Preview } from '../api';

export default function PreviewPanel({ preview }: { preview: Preview | null }) {
  if (!preview) return null;
  return <figure><img src={`/api/v1/images/${encodeURIComponent(preview.image_id)}`} alt="Превью CorelDRAW" />
    <figcaption data-testid="preview-binding">{preview.document_id} · {preview.target} · {preview.at}. Сохранённое превью, не live-canvas.</figcaption></figure>;
}
