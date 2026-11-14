import test from 'node:test';
import assert from 'node:assert/strict';
import { createExecutorFromEnvironment } from '../dist/backend.js';
import { CSharpExecutor } from '../dist/executor.js';

test('C# is the sole backend and the default never activates PowerShell', () => {
  const established = createExecutorFromEnvironment({});
  const development = createExecutorFromEnvironment({ CORELDRAW_MCP_BACKEND: 'csharp', CORELDRAW_MCP_VBA_OBSERVER: 'invalid-unused-legacy-value' });
  assert(established instanceof CSharpExecutor);
  assert(development instanceof CSharpExecutor);
  established.close(); development.close();
});

test('unsupported backend and incompatible C# ProgID fail without silently selecting another executor', () => {
  assert.throws(() => createExecutorFromEnvironment({ CORELDRAW_MCP_EXECUTION_MODE: 'typo' }), /режим|mode/i);
  assert.throws(() => createExecutorFromEnvironment({ CORELDRAW_MCP_BACKEND: 'typo' }), /backend|исполнител/i);
  assert.throws(() => createExecutorFromEnvironment({ CORELDRAW_MCP_BACKEND: 'csharp', CORELDRAW_MCP_PROGID: 'CorelDRAW.Application.26' }), /27/);
  assert.throws(() => createExecutorFromEnvironment({ CORELDRAW_MCP_BACKEND: 'powershell', CORELDRAW_MCP_VBA_OBSERVER: 'invalid' }));
  assert.throws(() => createExecutorFromEnvironment({ CORELDRAW_MCP_CSHARP_WORKER: 'worker.exe' }), /абсолютн/i);
});
