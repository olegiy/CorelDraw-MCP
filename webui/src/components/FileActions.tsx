import { useState } from 'react';
import { api } from '../api';
import type { components } from '../api.generated';

export default function FileActions({ disabled, hasDocument, rootId, setRootId, action, fail }: {
  disabled: boolean; hasDocument: boolean; rootId: string | null; setRootId: (id: string) => void;
  action: (tool: string, args?: Record<string, unknown>) => void; fail: (message: string) => void;
}) {
  const [directory, setDirectory] = useState(''); const [cdr, setCdr] = useState('');
  const [name, setName] = useState('result.png'); const [format, setFormat] = useState('png');
  const [range, setRange] = useState('page'); const [overwrite, setOverwrite] = useState(false);
  async function root() {
    try { const id = crypto.randomUUID(); const saved = await api<components['schemas']['Workspace']>('/workspace', { method: 'PUT', body: JSON.stringify({ output_roots: [{ id, path: directory }] }) }); const returned = saved.output_roots?.[0]?.id; if (!returned) throw new Error('API не вернул ID разрешённого каталога.'); setRootId(returned); }
    catch (error) { fail(error instanceof Error ? error.message : 'Каталог не разрешён.'); }
  }
  async function open() {
    try { const ref = await api<components['schemas']['PublicFileRef']>('/files/register', { method: 'POST', body: JSON.stringify({ path: cdr }) }); action('corel_open_document', { path: ref.file_ref }); }
    catch (error) { fail(error instanceof Error ? error.message : 'CDR не зарегистрирован.'); }
  }
  return <section className="panel document-panel"><h2>Файлы</h2>
    <button disabled={disabled || hasDocument} onClick={() => action('corel_create_document')}>Создать новый CDR</button>
    <label>Открыть локальный CDR<input value={cdr} disabled={disabled || hasDocument} onChange={event => setCdr(event.target.value)} /></label>
    <button disabled={disabled || hasDocument || !cdr} onClick={() => void open()}>Зарегистрировать и открыть CDR</button>
    <label>Каталог результатов<input value={directory} disabled={disabled} onChange={event => setDirectory(event.target.value)} /></label>
    <button disabled={disabled || !directory} onClick={() => void root()}>Разрешить каталог</button>
    <small>{rootId ? 'Операторский каталог выбран' : 'Каталог должен существовать; создание не выполняется'}</small>
    <label>Имя результата<input value={name} disabled={disabled} onChange={event => setName(event.target.value)} /></label>
    <label>Формат экспорта<select value={format} disabled={disabled} onChange={event => { setFormat(event.target.value); setName(`result.${event.target.value}`); }}><option value="png">PNG</option><option value="svg">SVG</option><option value="pdf">PDF</option></select></label>
    <label>Область экспорта<select value={range} disabled={disabled} onChange={event => setRange(event.target.value)}><option value="page">Страница</option><option value="selection">Выделение</option><option value="document">Документ (PDF)</option></select></label>
    <label><input type="checkbox" checked={overwrite} disabled={disabled} onChange={event => setOverwrite(event.target.checked)} />Запросить перезапись с подтверждением</label>
    <button disabled={disabled || !hasDocument || !rootId || !name} onClick={() => action('corel_export', { path: `@output/${name}`, format, range, overwrite })}>Экспортировать</button>
    <button disabled={disabled || !hasDocument || !rootId || !name.toLowerCase().endsWith('.cdr')} onClick={() => action('corel_save_document', { path: `@output/${name}`, overwrite })}>Сохранить как CDR</button>
  </section>;
}
