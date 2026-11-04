import { closeSync, fstatSync, lstatSync, mkdirSync, openSync, readSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { atomicJson } from './artifacts.js';
import { toolDefinitions } from './tools.js';
import { AutomationFailure, type ToolPolicySummary } from './types.js';

const mandatoryTools = new Set(['corel_status', 'corel_get_job', 'corel_guide', 'corel_list_artifacts']);
const knownTools = new Set([...Object.keys(toolDefinitions), 'corel_get_job', 'corel_guide']);
const maxConfigBytes = 65536;

export interface ToolPolicy {
  readonly source: ToolPolicySummary['source'];
  isEnabled(name: string): boolean;
  summary(): ToolPolicySummary;
}

function validate(value: unknown): Set<string> {
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      Object.keys(value).some((key) => key !== 'disabled_tools')) throw new Error('Invalid configuration');
  const disabled = (value as { disabled_tools?: unknown }).disabled_tools;
  if (!Array.isArray(disabled) || disabled.length > knownTools.size ||
      disabled.some((name) => typeof name !== 'string' || !knownTools.has(name)) ||
      new Set(disabled).size !== disabled.length) throw new Error('Invalid disabled_tools');
  return new Set(disabled.filter((name) => !mandatoryTools.has(name)));
}

// Unlike existsSync, lstat distinguishes missing files from unreadable paths and
// catches dangling links. A missing directory is allowed only under a safe parent.
function checkPath(path: string): void {
  let current = resolve(path);
  for (;;) {
    try {
      const stat = lstatSync(current);
      if (stat.isSymbolicLink() || (current !== resolve(path) && !stat.isDirectory()))
        throw new Error('Unsafe configuration path');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
    const parent = dirname(current);
    if (parent === current) return;
    current = parent;
  }
}

export function loadToolPolicy(directory: string): ToolPolicy {
  const configPath = join(resolve(directory), 'config.json');
  let source: ToolPolicySummary['source'] = 'default';
  let disabled = new Set<string>();
  let file: number | undefined;
  try {
    checkPath(configPath);
    try {
      const stat = lstatSync(configPath);
      if (!stat.isFile() || stat.size > maxConfigBytes) throw new Error('Invalid configuration file');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      return makePolicy('default', configPath, disabled);
    }
    file = openSync(configPath, 'r');
    const stat = fstatSync(file);
    if (!stat.isFile() || stat.size > maxConfigBytes) throw new Error('Invalid configuration file');
    const bytes = Buffer.alloc(maxConfigBytes + 1);
    let size = 0, count;
    while (size < bytes.length && (count = readSync(file, bytes, size, bytes.length - size, null)) > 0) size += count;
    if (size > maxConfigBytes) throw new Error('Configuration too large');
    disabled = validate(JSON.parse(bytes.subarray(0, size).toString('utf8')));
    source = 'user';
  } catch {
    source = 'invalid';
  } finally {
    if (file !== undefined) closeSync(file);
  }
  return makePolicy(source, configPath, disabled);
}

function makePolicy(source: ToolPolicySummary['source'], configPath: string, disabled: Set<string>): ToolPolicy {
  const enabled = new Set([...knownTools].filter((name) => mandatoryTools.has(name) ||
    (source === 'invalid' ? toolDefinitions[name]?.readOnly : !disabled.has(name))));
  return Object.freeze({
    source,
    isEnabled: (name: string) => enabled.has(name),
    summary: () => ({ source, config_path: configPath, disabled_count: knownTools.size - enabled.size,
      effective_count: enabled.size, registered_count: enabled.size, total_count: knownTools.size }),
  });
}

export function writeToolPolicy(directory: string, value: unknown): string {
  try {
    validate(value);
    const path = join(resolve(directory), 'config.json');
    checkPath(path);
    mkdirSync(dirname(path), { recursive: true });
    checkPath(path);
    atomicJson(path, value);
    return path;
  } catch (error) {
    throw new AutomationFailure('INVALID_TOOL_CONFIG', error instanceof Error ? error.message : String(error));
  }
}
