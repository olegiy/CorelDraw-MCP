import type { Executor } from './types.js';
import { CSharpExecutor } from './executor.js';
import { win32 } from 'node:path';

export function createExecutorFromEnvironment(environment: NodeJS.ProcessEnv = process.env): Executor {
  const mode = environment.CORELDRAW_MCP_EXECUTION_MODE ?? 'csharp';
  if (mode !== 'csharp' && mode !== 'hybrid') throw new Error('Допустим execution_mode csharp или hybrid.');
  if (environment.CORELDRAW_MCP_BACKEND !== undefined && environment.CORELDRAW_MCP_BACKEND !== 'csharp')
    throw new Error('Поддерживается только C# backend; прежний исполнитель не активируется.');
  if (environment.CORELDRAW_MCP_PROGID !== undefined && environment.CORELDRAW_MCP_PROGID !== 'CorelDRAW.Application.27')
    throw new Error('Исполнитель поддерживает только CorelDRAW.Application.27.');
  const worker = environment.CORELDRAW_MCP_CSHARP_WORKER;
  if (worker !== undefined && (!win32.isAbsolute(worker) || !/^[A-Za-z]:[\\/]/.test(worker) || /[\x00-\x1f]/.test(worker) || win32.extname(worker).toLowerCase() !== '.exe'))
    throw new Error('Нужен абсолютный локальный путь Windows к EXE исполнителя.');
  return new CSharpExecutor({ executionMode: mode, ...(environment.CORELDRAW_MCP_CSHARP_WORKER ? { workerPath: environment.CORELDRAW_MCP_CSHARP_WORKER } : {}) });
}
