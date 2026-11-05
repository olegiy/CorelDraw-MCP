import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { appendFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { JobManager } from '../../../dist/jobs.js';
import { createServer } from '../../../dist/server.js';

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAFklEQVR4nGP8//8/A27AhEeOYeRKAwCl4wMRx3ocVQAAAABJRU5ErkJggg==', 'base64');
const documents = ['a', 'b'].map(letter => ({ document_id: `fixture-${letter}`, name: `Рисунок ${letter.toUpperCase()}`, path: '', dirty: false, pages: 1, active_page: 1, unit: 3, preserve_selection: true }));
const executor = {
  async run(job, args, progress) {
    appendFileSync(join(process.env.CORELDRAW_MCP_RUNS, 'calls.jsonl'), JSON.stringify({ tool: job.tool, args }) + '\n');
    progress('fixture_running');
    await delay(process.argv.includes('--slow') ? 450 : 50);
    const cleanup = { completed: true, errors: [] };
    if (args.document_id && !documents.some(doc => doc.document_id === args.document_id))
      return { ok: false, error: { code: 'STALE_DOCUMENT', stage: 'fixture', message: 'Точный документ недоступен.', applied: false }, cleanup };
    if (job.tool === 'corel_status')
      return { ok: true, result: { version: '27.0', session_id: 'fixture', active_document_id: 'fixture-a', documents, vba: { available: true }, executor: { state: 'ready' } }, cleanup };
    if (job.tool === 'corel_inspect_document')
      return { ok: true, result: { document: documents.find(doc => doc.document_id === args.document_id), selection_count: 1, selection_static_ids: [1001], shapes: [{ static_id: 1001, name: 'Прямоугольник', bounds_mm: { left: 0, bottom: 0, width: 20, height: 30 } }], shapes_truncated: false, received: args }, cleanup };
    if (job.tool === 'corel_preview') {
      if (args.target === 'selection' && process.argv.includes('--empty-selection'))
        return { ok: false, error: { code: 'EMPTY_SELECTION', stage: 'preview', message: 'Выделение пусто.', applied: false }, cleanup };
      const path = join(job.run_directory, 'preview.png');
      writeFileSync(path, png);
      return { ok: true, result: { path, document_id: args.document_id, page_index: args.page_index ?? 1, target: args.target }, artifacts: [path], cleanup };
    }
    if (job.tool === 'corel_create_document' || job.tool === 'corel_open_document') {
      const doc = { document_id: `fixture-created-${documents.length}`, name: 'Новый рисунок', path: args.path ?? '', dirty: false, pages: 1, active_page: 1 };
      documents.push(doc);
      return { ok: true, result: doc, cleanup };
    }
    if (job.tool === 'corel_save_document' || job.tool === 'corel_export') {
      const doc = documents.find(doc => doc.document_id === args.document_id);
      const path = args.path ?? doc.path;
      writeFileSync(path, job.tool === 'corel_save_document' ? 'fixture CDR' : args.format === 'png' ? png : args.format === 'pdf' ? '%PDF-1.4\nfixture' : '<svg xmlns="http://www.w3.org/2000/svg"/>');
      if (job.tool === 'corel_save_document') { doc.path = path; doc.dirty = false; }
      return { ok: true, result: { applied: true, path, document_id: doc.document_id }, artifacts: [path], cleanup };
    }
    if (args.code === 'lose executor') throw new Error('Synthetic loss');
    if (args.code === 'fail') return { ok: false, error: { code: 'VBA_RUNTIME_ERROR', stage: 'executing_vba', message: 'Ошибка VBA.', applied: true }, cleanup };
    if (args.code === 'cleanup fail') return { ok: true, result: { applied: true }, cleanup: { completed: false, errors: [{ code: 'CLEANUP_FAILED', step: 'fixture' }] } };
    return { ok: true, result: { received: args, applied: true, document_id: args.document_id }, cleanup };
  },
  close() {},
};
const jobs = new JobManager(process.env.CORELDRAW_MCP_RUNS, executor);
serveStdio(() => createServer(jobs));
