import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

export type ToolPolicySource = 'default' | 'user' | 'invalid';

const readOnlyTools = new Set(['corel_status', 'corel_inspect_document', 'corel_describe_api', 'corel_measure_shapes', 'corel_get_job', 'corel_guide', 'corel_list_artifacts']);

export interface ToolPolicy {
  source: ToolPolicySource;
  configPath: string;
  disabledTools: ReadonlySet<string>;
  isEnabled(name: string): boolean;
}

export function loadToolPolicy(directory: string): ToolPolicy {
  const configPath = join(directory, 'config.json');
  if (!existsSync(configPath)) return makePolicy('default', configPath, new Set());
  try {
    const value = JSON.parse(readFileSync(configPath, 'utf8')) as unknown;
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('config must be an object');
    const disabled = (value as { disabled_tools?: unknown }).disabled_tools;
    if (!Array.isArray(disabled) || disabled.some((name) => typeof name !== 'string' || !/^corel_[a-z_]+$/.test(name)))
      throw new Error('disabled_tools must be an array of tool names');
    return makePolicy('user', configPath, new Set(disabled));
  } catch {
    return makePolicy('invalid', configPath, new Set());
  }
}

function makePolicy(source: ToolPolicySource, configPath: string, disabledTools: Set<string>): ToolPolicy {
  return {
    source,
    configPath,
    disabledTools,
    isEnabled(name) {
      if (source === 'invalid') return readOnlyTools.has(name);
      return !disabledTools.has(name) || readOnlyTools.has(name);
    },
  };
}
