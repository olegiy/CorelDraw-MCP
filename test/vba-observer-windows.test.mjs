import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { VbaObserver } from '../dist/vba-observer.js';

const root = fileURLToPath(new URL('../', import.meta.url));
const windows = process.platform === 'win32';
function fixture() {
  const child = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
    fileURLToPath(new URL('./vba-dialog.fixture.ps1', import.meta.url))],
  { windowsHide: true, shell: false, cwd: root, stdio: ['pipe', 'pipe', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', value => { stderr += value.toString(); });
  const lines = createInterface({ input: child.stdout });
  const waiting = [], buffered = [];
  lines.on('line', line => {
    const value = JSON.parse(line.replace(/^\uFEFF/, ''));
    if (waiting.length) waiting.shift().resolve(value); else buffered.push(value);
  });
  const next = () => buffered.length ? Promise.resolve(buffered.shift()) : new Promise((resolve, reject) => {
    const item = { resolve: value => { clearTimeout(timer); resolve(value); } };
    const timer = setTimeout(() => reject(new Error('Тайм-аут fixture: ' + stderr)), 15000);
    waiting.push(item);
  });
  return { child, next, send: async value => { child.stdin.write(JSON.stringify(value) + '\n'); return next(); },
    close: async () => {
      child.stdin.end();
      if (child.exitCode !== null) return;
      await new Promise(resolve => {
        const timer = setTimeout(() => { child.kill(); }, 3000); // Only this disposable fixture.
        child.once('exit', () => { clearTimeout(timer); lines.close(); resolve(); });
      });
    } };
}

test('Win32 adapter reads foreign dialog identity, owner chain and Unicode controls', { skip: !windows, timeout: 30000 }, async () => {
  assert(existsSync(new URL('../powershell/VbaDialogObserver.cs', import.meta.url)), 'Нужен независимый Win32-адаптер');
  const f = fixture();
  try {
    const ready = await f.next();
    const dialog = await f.send({ command: 'create', profile: 'compile', text: 'Ошибка компиляции: тест Ω' });
    const probe = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
      fileURLToPath(new URL('./vba-observer-probe.ps1', import.meta.url)), '-Hwnd', ready.hwnd],
    { windowsHide: true, shell: false, cwd: root });
    let output = '', error = '';
    probe.stdout.on('data', value => { output += value; });
    probe.stderr.on('data', value => { error += value; });
    const code = await new Promise(resolve => probe.once('exit', resolve));
    assert.equal(code, 0, error);
    const snapshot = JSON.parse(output.replace(/^\uFEFF/, ''));
    assert.equal(snapshot.identity.pid, ready.pid);
    assert(snapshot.identity.start_time);
    assert.equal(snapshot.identity.hwnd, ready.hwnd);
    const observed = snapshot.windows.find(window => window.hwnd === dialog.hwnd);
    assert(observed);
    assert.equal(observed.class_name, '#32770');
    assert(observed.owner_chain.includes(ready.hwnd));
    assert(observed.controls.some(control => control.text.includes('Ошибка компиляции')));
    assert(observed.controls.some(control => control.id === 1));
    assert.equal((await f.send({ command: 'stats' })).actions, 0);
    await f.send({ command: 'close', hwnd: dialog.hwnd });
  } finally { await f.close(); }
});

test('real observer publishes while another process waits and retains raw text only in its journal', { skip: !windows, timeout: 30000 }, async () => {
  const f = fixture();
  let observer;
  try {
    const ready = await f.next();
    const directory = mkdtempSync(join(tmpdir(), 'corelmcp-observer-native-'));
    const updates = [];
    observer = new VbaObserver({ job_id: 'fixture-native', run_directory: directory }, ready.pid, value => updates.push(value));
    assert.equal(await observer.start(), true);
    assert.equal((await observer.arm('compiling', 'CorelMcp_fixture', ready.hwnd)).available, true);
    const dialog = await f.send({ command: 'create', profile: 'runtime', text: 'PRIVATE_WINDOW_CANARY Ω' });
    const until = Date.now() + 6000;
    while (observer.summary.detected < 1 && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(observer.summary.detected, 1, JSON.stringify(observer.summary));
    assert.equal(observer.summary.requires_operator, 1);
    assert.equal(observer.summary.action_attempted, 0);
    assert(!JSON.stringify(updates).includes('PRIVATE_WINDOW_CANARY'));
    assert(readFileSync(join(directory, 'vba-observer.jsonl'), 'utf8').includes('PRIVATE_WINDOW_CANARY'));
    assert.equal((await f.send({ command: 'stats' })).actions, 0);
    assert.equal((await observer.disarm()).available, true);
    await f.send({ command: 'close', hwnd: dialog.hwnd });
  } finally { await observer?.stop(); await f.close(); }
});

test('baseline Save and MsgBox dialogs remain passive; manual close is not counted as dismissal', { skip: !windows, timeout: 30000 }, async () => {
  const f = fixture(); let observer;
  try {
    const ready = await f.next();
    const save = await f.send({ command: 'create', profile: 'save', text: 'Сохранить чужой проект?' });
    await f.send({ command: 'create', profile: 'msgbox', text: 'Неизвестная локаль / message' });
    const directory = mkdtempSync(join(tmpdir(), 'corelmcp-observer-baseline-'));
    observer = new VbaObserver({ job_id: 'fixture-baseline', run_directory: directory }, ready.pid, () => {});
    assert.equal(await observer.start(), true);
    assert.equal((await observer.arm('preparing_vba', undefined, ready.hwnd)).available, true);
    const until = Date.now() + 5000;
    while (observer.summary.detected < 2 && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(observer.summary.detected, 2);
    assert.equal(observer.summary.requires_operator, 2);
    await f.send({ command: 'close', hwnd: save.hwnd });
    assert.equal(observer.summary.dismissed, 0);
    assert.equal((await f.send({ command: 'stats' })).actions, 0);
    const journal = readFileSync(join(directory, 'vba-observer.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    assert(journal.every(event => event.baseline === true));
    assert.equal((await observer.disarm()).available, true);
    // The exact fixture process can disappear without the observer touching it.
    await f.close();
    const exited = Date.now() + 4000;
    while (observer.summary.state !== 'degraded' && Date.now() < exited) await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(observer.summary.state, 'degraded');
  } finally { await observer?.stop(); if (f.child.exitCode === null) await f.close(); }
});

test('a hung fixture UI cannot turn bounded control reads into an unbounded observer wait', { skip: !windows, timeout: 20000 }, async () => {
  const f = fixture();
  try {
    const ready = await f.next();
    await f.send({ command: 'create', profile: 'runtime', text: 'Зависший UI' });
    await f.send({ command: 'hang' });
    const started = Date.now();
    const probe = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
      fileURLToPath(new URL('./vba-observer-probe.ps1', import.meta.url)), '-Hwnd', ready.hwnd],
      { windowsHide: true, shell: false, cwd: root });
    let output = '', error = '';
    probe.stdout.on('data', value => { output += value; }); probe.stderr.on('data', value => { error += value; });
    assert.equal(await new Promise(resolve => probe.once('exit', resolve)), 0, error);
    assert(Date.now() - started < 4000);
    assert.equal(JSON.parse(output.replace(/^\uFEFF/, '')).truncated, true);
  } finally { await f.close(); }
});

test('journal I/O failure degrades only the observer and preserves the existing file and fixture process', { skip: !windows, timeout: 20000 }, async () => {
  const f = fixture(); let observer;
  try {
    const ready = await f.next();
    const directory = mkdtempSync(join(tmpdir(), 'corelmcp-observer-io-'));
    const path = join(directory, 'vba-observer.jsonl'); writeFileSync(path, 'EXISTING_PRIVATE_JOURNAL');
    const updates = [];
    observer = new VbaObserver({ job_id: 'fixture-io', run_directory: directory }, ready.pid, value => updates.push(value));
    assert.equal(await observer.start(), true);
    assert.equal((await observer.arm('compiling', 'CorelMcp_fixture', ready.hwnd)).available, false);
    assert.equal(observer.summary.state, 'degraded');
    assert.equal(readFileSync(path, 'utf8'), 'EXISTING_PRIVATE_JOURNAL');
    assert(!JSON.stringify(updates).includes(directory));
    assert.equal((await f.send({ command: 'stats' })).actions, 0);
  } finally { await observer?.stop(); await f.close(); }
});
