import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { VbaObserver, observerMode, safeObserverSummary, mergeObserverSummary } from '../dist/vba-observer.js';

const tick = () => new Promise(resolve => setTimeout(resolve, 10));
const identity = { hwnd: '42', pid: 123, tid: 456, start_time: '987654321', session: 1, desktop: 'Default' };
function fixture(options = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-observer-'));
  const child = Object.assign(new EventEmitter(), { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 123 });
  const commands = [], updates = [];
  child.kill = () => { child.emit('exit', 1); child.emit('close', 1); return true; };
  child.stdin.on('data', value => {
    const command = JSON.parse(value.toString()); commands.push(command);
    if (command.type === 'stop') queueMicrotask(() => {
      if (options.ackStop !== false) child.stdout.write(JSON.stringify({ ...command, type: 'stopped', sequence: 9999 }) + '\n');
      child.emit('exit', 0); child.emit('close', 0);
    });
  });
  const observer = new VbaObserver({ job_id: 'test-job', run_directory: directory }, 999, value => updates.push(value),
    { launch: () => child, startupMs: 100, handshakeMs: 50, ...options });
  let sequence = 0;
  const emit = (type, extra = {}) => {
    const command = commands.findLast(value => value.type === 'arm');
    child.stdout.write(JSON.stringify({ schema_version: 1, sequence: ++sequence, type,
      ...(command ? { job_id: command.job_id, generation: command.generation, token: command.token } : {}), ...extra }) + '\n');
  };
  return { observer, child, commands, updates, emit, directory };
}

test('host modes fail closed before any application work', () => {
  assert.equal(observerMode(undefined), 'observe');
  assert.equal(observerMode('off'), 'off');
  assert.throws(() => observerMode('compile-errors'), /не поддерживается/);
  assert.throws(() => observerMode('typo'), /Недопустимый/);
});

test('a missing stop acknowledgement is reported as degraded after the private process exits', async () => {
  const f = fixture({ ackStop: false }); const ready = f.observer.start();
  f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await ready;
  await f.observer.stop();
  assert.equal(f.observer.summary.state, 'stopped');
  assert.equal(f.observer.summary.last_code, 'VBA_OBSERVER_UNAVAILABLE');
  assert(f.updates.some(update => update.state === 'degraded'));
});

test('a changed process identity and a stalled arm revoke observation without restarting it', async () => {
  const f = fixture(); const ready = f.observer.start();
  f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await ready;
  const first = f.observer.arm('compiling', 'CorelMcp_one', identity); await tick(); f.emit('armed', { identity }); await first;
  const finish = f.observer.disarm(); await tick(); f.emit('disarmed'); await finish;
  const next = f.observer.arm('compiling', 'CorelMcp_two', identity); await tick();
  f.emit('armed', { identity: { ...identity, start_time: '111' } });
  assert.equal((await next).available, false);
  assert.equal(f.observer.summary.last_code, 'VBA_OBSERVER_LEASE_LOST');
  await f.observer.stop();
  const g = fixture(); const start = g.observer.start();
  g.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await start;
  assert.equal((await g.observer.arm('compiling', 'CorelMcp_one', identity)).available, false);
  assert.equal(g.observer.summary.state, 'degraded');
  g.emit('armed', { identity }); await tick();
  assert.equal(g.observer.summary.state, 'degraded');
  await g.observer.stop();
});

test('summary allowlist rejects raw data, bad numbers and external paths; merge never loses counters', () => {
  const summary = { schema_version: 1, mode: 'observe', state: 'observing', detected: 2,
    action_attempted: 0, dismissed: 0, requires_operator: 2, events_truncated: true };
  assert.deepEqual(safeObserverSummary({ ...summary, text: 'SECRET', token: 'TOKEN' }), summary);
  assert.equal(safeObserverSummary({ ...summary, detected: -1 }), undefined);
  assert.equal(safeObserverSummary({ ...summary, dismissed: 1 }), undefined);
  assert.equal(safeObserverSummary({ ...summary, schema_version: 2 }), undefined);
  assert.deepEqual(mergeObserverSummary(summary, { ...summary, detected: 0, requires_operator: 0, events_truncated: false }), summary);
});

test('independent events update summary while worker is blocked; tokens and raw controls stay private', async () => {
  const f = fixture();
  const start = f.observer.start(); f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await start;
  const arm = f.observer.arm('compiling', 'CorelMcp_fixture', identity); await tick(); f.emit('armed', { identity });
  assert.equal((await arm).available, true);
  f.emit('observation', { candidate_id: 'window-1', code: 'VBA_DIALOG_REQUIRES_OPERATOR', artifact: join(f.directory, 'vba-observer.jsonl'), text: 'SECRET', controls: ['SOURCE'] });
  await tick();
  assert.equal(f.observer.summary.detected, 1);
  assert.equal(f.observer.summary.requires_operator, 1);
  assert(!JSON.stringify(f.updates).includes('SECRET'));
  assert(!JSON.stringify(f.updates).includes('token'));
  const disarm = f.observer.disarm(); await tick(); f.emit('disarmed'); await disarm;
  await f.observer.stop();
  assert.equal(f.observer.summary.state, 'stopped');
});

test('old leases, duplicate candidates and malformed events cannot alter outcome or inflate counters', async () => {
  const f = fixture();
  const start = f.observer.start(); f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await start;
  const arm = f.observer.arm('compiling', 'CorelMcp_fixture', identity); await tick(); f.emit('armed', { identity }); await arm;
  f.emit('observation', { candidate_id: 'one', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  f.emit('observation', { candidate_id: 'one', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  f.emit('observation', { generation: -1, candidate_id: 'stale', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  f.emit('observation', { token: 'foreign', candidate_id: 'token', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  f.emit('observation', { job_id: 'foreign', candidate_id: 'job', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  f.emit('observation', { sequence: 1, candidate_id: 'old-sequence', code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  await tick(); assert.equal(f.observer.summary.detected, 1);
  f.child.stdout.write('invalid-json\n'); await tick();
  assert.equal(f.observer.summary.state, 'degraded');
  assert.equal(f.observer.summary.last_code, 'VBA_OBSERVER_PROTOCOL_ERROR');
  await f.observer.stop();
});

test('event, stderr and incomplete UTF-8 limits fail closed without publishing raw data', async () => {
  const f = fixture(); const start = f.observer.start();
  f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await start;
  const arm = f.observer.arm('compiling', 'CorelMcp_many', identity); await tick(); f.emit('armed', { identity }); await arm;
  for (let i = 0; i < 101; i++) f.emit('observation', { candidate_id: 'candidate-' + i, code: 'VBA_DIALOG_REQUIRES_OPERATOR' });
  assert.equal(f.observer.summary.detected, 100);
  assert.equal(f.observer.summary.events_truncated, true);
  assert.equal(f.observer.summary.state, 'degraded');
  assert(f.updates.length < 10, 'Публикация событий должна объединяться, а не перезаписывать report на каждое окно');
  await f.observer.stop();
  const g = fixture(); const ready = g.observer.start();
  g.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await ready;
  g.child.stderr.write('PRIVATE_CANARY'.repeat(6000));
  assert.equal(g.observer.summary.state, 'degraded');
  assert(!JSON.stringify(g.updates).includes('PRIVATE_CANARY')); await g.observer.stop();
  const h = fixture(); const pending = h.observer.start();
  h.child.stdout.end(Buffer.from([0xf0, 0xff]));
  assert.equal(await pending, false);
  assert.equal(h.observer.summary.last_code, 'VBA_OBSERVER_PROTOCOL_ERROR'); await h.observer.stop();
});

test('oversized unfinished frames and EOF are bounded; observer failure never throws into update callback', async () => {
  const f = fixture();
  const start = f.observer.start(); f.emit('ready', { owner: { pid: process.pid, start_time: '123' }, worker: { pid: 999, start_time: '456' } }); await start;
  f.child.stdout.write('x'.repeat(65537)); await tick();
  assert.equal(f.observer.summary.state, 'degraded');
  await f.observer.stop();
  const g = fixture(); const pending = g.observer.start(); g.child.emit('exit', 1);
  assert.equal(await pending, false);
});
