// Opt-in live smoke. No action profiles are enabled by this harness.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';
import { Client } from '@modelcontextprotocol/client';

if (process.env.CORELDRAW_MCP_LIVE_OBSERVER !== '1') {
  console.log('Пропуск: живой observe smoke требует CORELDRAW_MCP_LIVE_OBSERVER=1. CorelDRAW не запускался.');
  process.exit(0);
}
if (process.platform !== 'win32') throw new Error('Живой observe smoke требует Windows.');

// This transport never uses the SDK stdio transport's force-kill fallback.
class DrainTransport {
  unresolved = false;
  async start() {
    this.child = spawn(process.execPath, [fileURLToPath(new URL('../dist/index.js', import.meta.url))],
      { env: { ...process.env, CORELDRAW_MCP_RUNS: runs, CORELDRAW_MCP_VBA_OBSERVER: 'observe' },
        shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.stderr.on('data', () => {});
    this.lines = createInterface({ input: this.child.stdout });
    this.lines.on('line', line => { try { this.onmessage?.(JSON.parse(line)); } catch { this.onerror?.(new Error('Некорректный MCP JSON.')); } });
    this.child.once('error', error => this.onerror?.(error));
    this.child.once('exit', () => this.onclose?.());
  }
  async send(message) {
    await new Promise((resolve, reject) => this.child.stdin.write(JSON.stringify(message) + '\n', error => error ? reject(error) : resolve()));
  }
  async close() {
    if (!this.child?.pid) return;
    this.child.stdin.end();
    if (this.unresolved) {
      // EOF lets the owned server drain. Retain its report and leave COM alone.
      this.child.unref();
      for (const stream of [this.child.stdin, this.child.stdout, this.child.stderr]) stream.unref?.();
      return;
    }
    if (this.child.exitCode === null) await new Promise(resolve => this.child.once('exit', resolve));
    this.lines.close();
  }
}

const root = await mkdtemp(join(process.env.CORELDRAW_MCP_INTEGRATION_PARENT || process.env.LOCALAPPDATA || tmpdir(), 'CorelDrawMcp-observer-'));
const runs = join(root, 'runs');
const transport = new DrainTransport();
const client = new Client({ name: 'observer-live-smoke', version: '1.0.0' });
const owned = new Set(), retained = [], checks = [], reports = [];
let failure, before, after, document;
const request = () => ({ request_id: 'observer-' + randomUUID() });
const data = response => response.structuredContent ?? JSON.parse(response.content.find(block => block.type === 'text').text);
async function operation(tool, args = {}) {
  let report = data(await client.callTool({ name: tool, arguments: { ...args, deadline_seconds: 30 } }));
  if (!report.job_id) throw new Error('MCP не вернул job_id: ' + JSON.stringify(report));
  const deadline = Date.now() + 35000;
  while (!report.finished_at) {
    if (Date.now() > deadline || report.status === 'unknown') {
      transport.unresolved = true;
      throw new Error('Исход не подтверждён: ' + report.job_id + '. Закройте только тестовое окно вручную; повтор запрещён. Отчёт: ' + report.report_path);
    }
    report = data(await client.callTool({ name: 'corel_get_job', arguments: { job_id: report.job_id, wait_seconds: 1 } }));
  }
  reports.push(report);
  if (report.cleanup?.project_retained) retained.push({ name: report.cleanup.project_name,
    path: report.artifacts.find(path => path.endsWith(report.cleanup.project_name + '.gms')) });
  assert.equal(report.vba_observer.mode, 'observe');
  assert.equal(report.vba_observer.action_attempted, 0);
  assert.equal(report.vba_observer.dismissed, 0);
  return report;
}
async function ok(tool, args) {
  const report = await operation(tool, args);
  assert.equal(report.status, 'succeeded', JSON.stringify(report));
  assert.equal(report.cleanup.completed, true, JSON.stringify(report));
  return report;
}
try {
  await client.connect(transport);
  before = (await ok('corel_status')).result;
  for (const [name, filter] of [['Application', 'AppWindow'], ['AppWindow', 'Handle']]) {
    const metadata = await ok('corel_describe_api', { kind: 'members', name, filter });
    assert(metadata.result.items.length > 0); checks.push('Метаданные ' + name + '.' + filter);
  }
  document = (await ok('corel_create_document', { ...request(), width_mm: 20, height_mm: 20 })).result.document_id;
  owned.add(document);
  const source = 'Option Explicit\nPublic Function Main() As String\nMain="{""ok"":true}"\nEnd Function';
  const result = await ok('corel_execute_vba', { ...request(), document_id: document, code: source });
  assert.equal(result.result.value.ok, true);
  assert.notEqual(result.vba_observer.last_code, 'VBA_OBSERVER_UNAVAILABLE');
  assert(!result.warnings.some(value => value.startsWith('Наблюдатель VBA:')));
  checks.push('Успешная VBA-операция с observe и нулевыми action counters');
  if (process.env.CORELDRAW_MCP_LIVE_OBSERVER_INTERACTIVE === '1') {
    console.log('Интерактивный Err.Raise: если VBE прервёт выполнение, закройте только тестовый диалог.');
    const runtime = await operation('corel_execute_vba', { ...request(), document_id: document,
      code: 'Option Explicit\nPublic Function Main() As String\nErr.Raise 555, "ObserverFixture", "Тестовая ошибка"\nEnd Function' });
    assert.equal(runtime.error.code, 'VBA_RUNTIME_ERROR'); checks.push('Обработанный Err.Raise, без автоматического закрытия');
  }
  await ok('corel_execute_vba', { ...request(), document_id: document, code: source });
  checks.push('Следующая операция после фактического результата');
} catch (error) { failure = error; }
finally {
  if (!transport.unresolved) {
    // Close exact documents created by this harness, even after a test failure.
    for (const document_id of owned) {
      try { await ok('corel_close_document', { ...request(), document_id, discard_changes: true }); owned.delete(document_id); }
      catch (error) { failure ??= error; if (transport.unresolved) break; }
    }
    if (!transport.unresolved && retained.length) {
      const ledger = join(root, 'retained-projects.json'); await writeFile(ledger, JSON.stringify(retained));
      const child = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-STA', '-File',
        fileURLToPath(new URL('./integration-vba-observer-cleanup.ps1', import.meta.url)), '-Ledger', ledger],
      { windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'] });
      const code = await new Promise(resolve => child.once('exit', resolve));
      if (code !== 0) failure ??= new Error('Очистка точных тестовых GMS не подтверждена; см. ledger.');
    }
    if (!transport.unresolved && before) {
      try { after = (await ok('corel_status')).result; assert.deepEqual(after.documents, before.documents); }
      catch (error) { failure ??= error; }
    }
  }
  await writeFile(join(root, 'verification.json'), JSON.stringify({ ok: !failure, date: new Date().toISOString(), checks,
    mode: 'observe', action_profiles_verified: false, interactive_runtime: process.env.CORELDRAW_MCP_LIVE_OBSERVER_INTERACTIVE === '1',
    modal_profiles: 'Не проверялись: отдельное исследование с оператором', user_shape_state_verified: false,
    before, after, reports, retained, unresolved: transport.unresolved, cleanup_completed: !owned.size && !transport.unresolved,
    error: failure?.message }, null, 2));
  console.log('Отчёт observe smoke: ' + join(root, 'verification.json'));
  await client.close();
}
if (failure) throw failure;
