import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { appendFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { JobManager } from '../../../dist/jobs.js';
import { createServer } from '../../../dist/server.js';

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAFklEQVR4nGP8//8/A27AhEeOYeRKAwCl4wMRx3ocVQAAAABJRU5ErkJggg==', 'base64');
const documents = ['a', 'b'].map(letter => ({ document_id: `fixture-${letter}`, name: `Рисунок ${letter.toUpperCase()}`, path: '', dirty: false, pages: 1, active_page: 1, unit: 3, preserve_selection: true }));
const smoke = process.argv.includes('--panel-smoke');
const shapes = new Map(documents.map(doc => [doc.document_id, [{ static_id: 1001, name: 'Baseline', bounds_mm: { left: 0, bottom: 0, width: 20, height: 30 } }]]));
const executor = {
  async run(job, args, progress) {
    appendFileSync(join(process.env.CORELDRAW_MCP_RUNS, 'calls.jsonl'), JSON.stringify({ tool: job.tool, args }) + '\n');
    progress('fixture_running');
    await delay(process.argv.includes('--slow') ? 450 : 50);
    const cleanup = { completed: true, errors: [] };
    if (args.document_id && !documents.some(doc => doc.document_id === args.document_id))
      return { ok: false, error: { code: 'STALE_DOCUMENT', stage: 'fixture', message: 'Точный документ недоступен.', applied: false }, cleanup };
    if (smoke && job.tool === 'corel_status' && process.argv.includes('--smoke-baseline_failure'))
      return { ok: false, error: { code: 'STATUS_FAILED', message: 'Synthetic baseline failure', applied: false }, cleanup };
    if (job.tool === 'corel_status')
      return { ok: true, versions: { coreldraw: process.argv.includes('--smoke-wrong_version') ? '26.0.0' : '27.2.0.135' },
        result: { version: process.argv.includes('--smoke-wrong_version') ? 'Версия 26.0.0'
        : process.argv.includes('--smoke-localized_version') ? 'Версия 27.2.0.135' : '27.0',
        session_id: 'fixture', active_document_id: 'fixture-a', documents,
        vba: { available: true, compile_available: true }, executor: { state: 'ready' },
        application_state: { optimization: false, events_enabled: true } }, cleanup };
    if (job.tool === 'corel_inspect_document' && smoke) {
      const current = shapes.get(args.document_id) ?? [];
      return { ok: true, result: { document: documents.find(doc => doc.document_id === args.document_id),
        shapes: current, total_shapes: current.length, selection_ids: current.map(shape => shape.static_id),
        total_selected: current.length, next_offset: null, shapes_truncated: false, pages_truncated: false,
        layers_truncated: false, selection_truncated: false, depth_truncated: false, visited_truncated: false }, cleanup };
    }
    if (job.tool === 'corel_inspect_document')
      return { ok: true, result: { document: documents.find(doc => doc.document_id === args.document_id), selection_count: 1, selection_static_ids: [1001], shapes: [{ static_id: 1001, name: 'Прямоугольник', bounds_mm: { left: 0, bottom: 0, width: 20, height: 30 } }], shapes_truncated: false, received: args }, cleanup };
    if (job.tool === 'corel_preview') {
      if (args.target === 'selection' && (process.argv.includes('--empty-selection') || (smoke && !shapes.get(args.document_id)?.length)))
        return { ok: false, error: { code: 'EMPTY_SELECTION', stage: 'preview', message: 'Выделение пусто.', applied: false }, cleanup };
      const path = join(job.run_directory, 'preview.png');
      writeFileSync(path, smoke && process.argv.includes('--smoke-bad_png') ? Buffer.from('not a PNG') : png);
      return { ok: true, result: smoke ? { path, bytes: png.length, format: 'png', range: args.target ?? 'page' }
        : { path, document_id: args.document_id, page_index: args.page_index ?? 1, target: args.target }, artifacts: [path], cleanup };
    }
    if (job.tool === 'corel_create_document' || job.tool === 'corel_open_document') {
      const doc = { document_id: `fixture-created-${documents.length}`, name: 'Новый рисунок', path: args.path ?? '', dirty: false, pages: 1, active_page: 1 };
      documents.push(doc);
      if (smoke) shapes.set(doc.document_id, []);
      if (smoke && process.argv.includes('--smoke-unknown_creation'))
        return { ok: true, result: { name: doc.name }, cleanup };
      return { ok: true, result: doc, cleanup };
    }
    if (smoke && job.tool === 'corel_close_document') {
      documents.splice(documents.findIndex(doc => doc.document_id === args.document_id), 1);
      shapes.delete(args.document_id);
      return { ok: true, result: { applied: true, document_id: args.document_id }, cleanup };
    }
    if (smoke && job.tool === 'corel_execute_vba') {
      const project = 'CorelMcp_fixture_' + job.job_id;
      const inventory = ['CorelMcp_previous_diagnostic', 'UserGMS_keep', project];
      const vbaCleanup = { ...cleanup, project_name: project, project_retained: false };
      if (process.argv.includes('--smoke-retained_gms'))
        return { ok: false, error: { code: 'VBA_RUNTIME_ERROR', message: 'Diagnostic GMS retained', applied: true },
          cleanup: { ...vbaCleanup, project_retained: true } };
      if (process.argv.includes('--smoke-cleanup_failure')) {
        vbaCleanup.completed = false;
        vbaCleanup.errors.push({ stage: 'restore', message: 'Synthetic cleanup failure' });
      }
      if (process.argv.includes('--smoke-vba_failure'))
        return { ok: false, error: { code: 'VBA_RUNTIME_ERROR', message: 'Synthetic VBA failure', applied: true }, cleanup: vbaCleanup };
      if (args.code.includes('CorelMcpPanelSmoke: geometry v1')) {
        const shape = { static_id: 2000 + documents.indexOf(documents.find(doc => doc.document_id === args.document_id)),
          name: 'CorelMcp panel smoke', bounds_mm: { left: 10, bottom: 10, width: 20, height: 30 } };
        shapes.set(args.document_id, [shape]);
        return { ok: true, result: { value: { projects: inventory, static_id: shape.static_id }, applied: true }, cleanup: vbaCleanup };
      }
      if (args.code.includes('CorelMcpPanelSmoke: inventory v1'))
        return { ok: true, result: { value: { projects: inventory }, applied: true }, cleanup: vbaCleanup };
      throw new Error('Unexpected smoke VBA');
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
