import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { appendFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { JobManager } from '../../../dist/jobs.js';
import { createServer } from '../../../dist/server.js';

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAIAAAACUFjqAAAAFklEQVR4nGP8//8/A27AhEeOYeRKAwCl4wMRx3ocVQAAAABJRU5ErkJggg==', 'base64');
const documents = ['a', 'b'].map(letter => ({ document_id: `fixture-${letter}`, name: `Рисунок ${letter.toUpperCase()}`, path: '', dirty: false, pages: 1, active_page: 1, unit: 3, preserve_selection: true }));
const smoke = process.argv.includes('--panel-smoke');
const temporaryProjects = { complete: !process.argv.includes('--inventory-incomplete'),
  idle: !process.argv.includes('--inventory-busy'), gms: [], vbe_names: [] };
const retainedName = 'CorelMcp_a1b2c3d4e5f6';
if (process.argv.includes('--inventory-loaded')) temporaryProjects.gms.push({name: retainedName.toUpperCase(), path: 'C:\\other.gms'});
if (process.argv.includes('--inventory-renamed')) temporaryProjects.gms.push({name: 'Renamed', path: `C:\\runs\\00000000-0000-4000-8000-000000000001\\${retainedName}.gms`});
if (process.argv.includes('--inventory-path-alias')) temporaryProjects.gms.push({name: 'Renamed', path: `\\\\?\\C:\\runs\\00000000-0000-4000-8000-000000000001\\${retainedName}.gms`});
if (process.argv.includes('--inventory-vbe')) temporaryProjects.vbe_names.push(retainedName);
if (process.argv.includes('--inventory-malformed')) temporaryProjects.gms.push({name: 'Missing path'});
const shapes = new Map(documents.map(doc => [doc.document_id, [{ static_id: 1001, name: 'Baseline', bounds_mm: { left: 0, bottom: 0, width: 20, height: 30 } }]]));
const executor = {
  backend: 'csharp',
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
        vba: { available: true, compile_available: true,
          ...(process.argv.includes('--inventory-omitted') ? {} : {temporary_projects: temporaryProjects}) }, executor: { state: 'ready', backend: 'csharp' },
        capabilities: { typed_operations: true, arbitrary_code: false },
        application_state: { optimization: false, events_enabled: true } }, cleanup };
    if (job.tool === 'corel_inspect_document' && smoke) {
      const current = shapes.get(args.document_id) ?? [];
      return { ok: true, result: { document: documents.find(doc => doc.document_id === args.document_id),
        shapes: current, total_shapes: current.length, selection_ids: [],
        layers: [{ layer_id: `layer-${args.document_id}`, is_special_layer: false }],
        total_selected: 0, next_offset: null, shapes_truncated: false, pages_truncated: false,
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
    if (job.tool === 'corel_layout_shapes') return { ok: true, result: {
      document_id: args.document_id, dry_run: args.dry_run, layout_revision: '1'.repeat(64),
      placements: [], unplaced: [], received: args,
    }, cleanup };
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
    if (smoke && job.tool === 'corel_create_shapes') {
      if (process.argv.includes('--smoke-retained_gms'))
        return { ok: false, error: { code: 'UNEXPECTED_LEGACY_CLEANUP', message: 'Historical cleanup evidence retained', applied: true },
          cleanup: { ...cleanup, project_name: 'CorelMcp_fixture_historical', project_retained: true } };
      if (process.argv.includes('--smoke-drawing_failure'))
        return { ok: false, error: { code: 'DRAWING_FAILED', message: 'Synthetic drawing failure', applied: true }, cleanup };
      const observedCleanup = process.argv.includes('--smoke-cleanup_failure')
        ? { completed: false, errors: [{ stage: 'restore', message: 'Synthetic cleanup failure' }] } : cleanup;
      const shape = { static_id: 2000 + documents.findIndex(doc => doc.document_id === args.document_id),
        name: 'CorelMcp panel smoke', bounds_mm: { left: 10, bottom: 10, width: 20, height: 30 } };
      shapes.set(args.document_id, [shape]);
      return { ok: true, result: { created_static_ids: [shape.static_id], items: [shape], applied: true }, cleanup: observedCleanup };
    }
    if (job.tool === 'corel_save_document' || job.tool === 'corel_export') {
      const doc = documents.find(doc => doc.document_id === args.document_id);
      const path = args.path ?? doc.path;
      writeFileSync(path, job.tool === 'corel_save_document' ? 'fixture CDR' : args.format === 'png' ? png : args.format === 'pdf' ? '%PDF-1.4\nfixture' : '<svg xmlns="http://www.w3.org/2000/svg"/>');
      if (job.tool === 'corel_save_document') { doc.path = path; doc.dirty = false; }
      return { ok: true, result: { applied: true, path, document_id: doc.document_id }, artifacts: [path], cleanup };
    }
    if (args.text === 'lose executor') throw new Error('Synthetic loss');
    if (args.text === 'fail') return { ok: false, error: { code: 'DRAWING_FAILED', stage: 'drawing', message: 'Ошибка операции.', applied: true }, cleanup };
    if (args.text === 'cleanup fail') return { ok: true, result: { applied: true }, cleanup: { completed: false, errors: [{ code: 'CLEANUP_FAILED', step: 'fixture' }] } };
    return { ok: true, result: { received: args, applied: true, document_id: args.document_id }, cleanup };
  },
  close() {},
};
const jobs = new JobManager(process.env.CORELDRAW_MCP_RUNS, executor);
serveStdio(() => createServer(jobs));
