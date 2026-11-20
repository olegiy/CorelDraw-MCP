import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

// These are source-level release guards, not a substitute for a CorelDRAW run.
const source = readFileSync(new URL('../tools/duplicate-demo/manual/M_McpDuplicateDemo.bas', import.meta.url), 'utf8');

test('the manual duplicate module does not open files for logging', () => {
  assert.doesNotMatch(source, /^\s*Open\s+.+\s+For\s+(?:Append|Output|Binary|Random)\b/im);
});

test('the manual duplicate module leaves Undo to the operator', () => {
  assert.doesNotMatch(source, /\.Undo\b/i);
});

test('the manual duplicate module rejects a page mismatch before mutation', () => {
  const preflight = source.split(/doc\.BeginCommandGroup/i)[0];
  assert.match(preflight, /If original\.Page\.Index <> doc\.ActivePage\.Index Then\s+[^\r\n]+\s+Exit Function/i);
});

test('a possibly applied group close is reported without retrying', () => {
  assert.match(source, /closePending = False\s+closeUnconfirmed = True\s+doc\.EndCommandGroup\s+closeUnconfirmed = False/i);
  assert.match(source, /If closeUnconfirmed Then\s+[^\r\n]+closing the Undo group/i);
});
