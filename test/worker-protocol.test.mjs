import test from 'node:test';
import assert from 'node:assert/strict';
import { JsonLineDecoder, parseProtocolJson, encodeProtocolJson, MAX_WORKER_RECORD_BYTES } from '../dist/worker-protocol.js';

test('worker JSON refuses duplicate names at any depth including escaped aliases', () => {
  for (const input of ['{"x":1,"x":2}', '{"args":{"x":1,"\\u0078":2}}', '[{"x":1,"x":2}]'])
    assert.throws(() => parseProtocolJson(input), /повтор/i);
  assert.deepEqual(parseProtocolJson('{"x":"{\\\"x\\\":1}","y":{"x":2}}'), { x: '{"x":1}', y: { x: 2 } });
});

test('worker JSON rejects nonfinite values and invalid Unicode without changing valid emoji', () => {
  for (const input of ['{"x":1e400}', '{"x":NaN}', '{"x":"\\ud800"}', '{"x":"\\udc00"}'])
    assert.throws(() => parseProtocolJson(input));
  assert.deepEqual(parseProtocolJson('{"x":"Ёж 🦔"}'), { x: 'Ёж 🦔' });
  for (const value of [{ x: Infinity }, { x: NaN }, { x: undefined }, { x: 1n }, { x: new Date() }])
    assert.throws(() => encodeProtocolJson(value));
  const cyclic = {}; cyclic.self = cyclic;
  assert.throws(() => encodeProtocolJson(cyclic));
});

test('JSONL buffers fragmented UTF-8 and accepts CRLF independently of chunk boundaries', () => {
  const messages = [], errors = [];
  const reader = new JsonLineDecoder(value => messages.push(value), message => errors.push(message));
  const data = Buffer.from('{"x":"Ёж 🦔"}\r\n{"x":2}\n');
  for (const byte of data) reader.feed(Buffer.from([byte]));
  reader.end();
  assert.deepEqual(messages, [{ x: 'Ёж 🦔' }, { x: 2 }]);
  assert.deepEqual(errors, []);
});

test('bad UTF-8, noise and truncated EOF never become a completed frame', () => {
  const messages = [], errors = [];
  const reader = new JsonLineDecoder(value => messages.push(value), message => errors.push(message));
  reader.feed(Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xc3, 0x28, 0x22, 0x7d, 0x0a]));
  reader.feed(Buffer.from('noise\n{"partial":'));
  reader.end();
  assert.deepEqual(messages, []);
  assert.equal(errors.length, 3);
  assert(!errors.join('').includes('noise'));
});

test('JSONL drops an oversized frame with bounded storage and recovers at the next newline', () => {
  const messages = [], errors = [];
  const reader = new JsonLineDecoder(value => messages.push(value), message => errors.push(message));
  reader.feed(Buffer.alloc(MAX_WORKER_RECORD_BYTES + 50, 0x20));
  reader.feed(Buffer.from('\n{"ok":true}\n'));
  reader.end();
  assert.equal(errors.length, 1);
  assert.deepEqual(messages, [{ ok: true }]);
  assert.throws(() => encodeProtocolJson({ x: 'a'.repeat(MAX_WORKER_RECORD_BYTES) }), /4 МиБ/);
});

test('JSONL enforces the exact byte boundary and rejects excessive nesting', () => {
  const messages = [], errors = [];
  const reader = new JsonLineDecoder(value => messages.push(value), message => errors.push(message));
  const exact = '"' + 'a'.repeat(MAX_WORKER_RECORD_BYTES - 2) + '"';
  reader.feed(Buffer.from(exact + '\r\n'));
  reader.end();
  assert.equal(messages[0].length, MAX_WORKER_RECORD_BYTES - 2);
  assert.deepEqual(errors, []);
  assert.throws(() => parseProtocolJson('['.repeat(65) + '0' + ']'.repeat(65)), /глубин/i);
});
