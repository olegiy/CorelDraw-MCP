import { useEffect, useState } from 'react';
import { api } from '../api';
import type { components } from '../api.generated';

export default function FileActions({ disabled, hasDocument, outputDirectory, setOutputDirectory, action, fail }: {
  disabled: boolean; hasDocument: boolean; outputDirectory: string | null; setOutputDirectory: (path: string | null) => void;
  action: (tool: string, args?: Record<string, unknown>) => void; fail: (message: string) => void;
}) {
  const [directory, setDirectory] = useState(''); const [cdr, setCdr] = useState('');
  const [name, setName] = useState('result.png'); const [format, setFormat] = useState('png');
  const [range, setRange] = useState('page');
  useEffect(() => { setDirectory(outputDirectory ?? ''); }, [outputDirectory]);
  const destination = /^(?:[A-Za-z]:[\\/]|[\\/])/.test(name) ? name
    : outputDirectory && name ? `${outputDirectory.replace(/[\\/]+$/, '')}\\${name}` : null;
  async function selectDirectory() {
    try { const saved = await api<components['schemas']['Workspace']>('/workspace', { method: 'PUT', body: JSON.stringify({ output_directory: directory || null }) }); setOutputDirectory(saved.output_directory ?? null); }
    catch (error) { fail(error instanceof Error ? error.message : 'Каталог назначения недоступен.'); }
  }
  return <details className="context-disclosure file-disclosure"><summary>Файлы</summary><section className="panel document-panel disclosure-content"><h2>Файлы</h2>
    <button disabled={disabled} onClick={() => action('corel_create_document')}>Создать новый CDR</button>
    <label>Открыть локальный CDR<input value={cdr} disabled={disabled} onChange={event => setCdr(event.target.value)} /></label>
    <button disabled={disabled || !cdr} onClick={() => action('corel_open_document', { path: cdr })}>Открыть CDR</button>
    <label>Каталог результатов<input value={directory} disabled={disabled} onChange={event => setDirectory(event.target.value)} /></label>
    <button disabled={disabled} onClick={() => void selectDirectory()}>Выбрать каталог</button>
    <small>{outputDirectory ? 'Каталог результатов выбран' : 'Для относительного имени выберите существующий каталог; абсолютный путь не требует выбора'}</small>
    <label>Имя результата<input value={name} disabled={disabled} onChange={event => setName(event.target.value)} /></label>
    <label>Формат экспорта<select value={format} disabled={disabled} onChange={event => { setFormat(event.target.value); setName(`result.${event.target.value}`); setRange('page'); }}><option value="png">PNG</option><option value="svg">SVG</option><option value="pdf">PDF</option></select></label>
    <label>Область экспорта<select value={range} disabled={disabled} onChange={event => setRange(event.target.value)}><option value="page">Страница</option>{format === 'pdf' ? <option value="document">Документ (PDF)</option> : <option value="selection">Выделение</option>}</select></label>
    <p className="muted">Существующие файлы Save As и экспорта заменяются автоматически.</p>
    <button disabled={disabled || !hasDocument || !destination} onClick={() => action('corel_export', { path: destination, format, range })}>Экспортировать</button>
    <button disabled={disabled || !hasDocument || !destination || !name.toLowerCase().endsWith('.cdr')} onClick={() => action('corel_save_document', { path: destination })}>Сохранить как CDR</button>
  </section></details>;
}
