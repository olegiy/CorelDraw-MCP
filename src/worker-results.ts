import { lstatSync, readFileSync } from 'node:fs';
import { isAbsolute, relative } from 'node:path';
import { assertNoLinks } from './artifacts.js';
import { measureSnapshot } from './geometry.js';
import { AutomationFailure, type Arguments, type Job, type WorkerResult } from './types.js';

export function postprocessWorkerResult(result: WorkerResult, job: Job, args: Arguments, progress: (stage: string) => void): WorkerResult {
  if (!result.ok || job.tool !== 'corel_measure_shapes') return result;
  try {
    const snapshotPath = (result.result as { snapshot_path?: unknown })?.snapshot_path;
    if (typeof snapshotPath !== 'string') throw new AutomationFailure('GEOMETRY_INVALID_INPUT', 'Исполнитель не вернул файл геометрии.');
    const rel = relative(job.run_directory, snapshotPath);
    if (isAbsolute(rel) || rel === '..' || rel.startsWith('..\\') || rel.startsWith('../') || !result.artifacts?.includes(snapshotPath))
      throw new AutomationFailure('GEOMETRY_INVALID_PATH', 'Файл геометрии вне каталога задания.');
    assertNoLinks(snapshotPath);
    const info = lstatSync(snapshotPath);
    if (!info.isFile() || info.size > 64 * 1024 * 1024)
      throw new AutomationFailure('GEOMETRY_LIMIT_EXCEEDED', 'Файл геометрии превышает 64 МиБ.');
    progress('calculating_geometry');
    const details = result.result as Record<string, unknown>;
    result.result = {
      ...measureSnapshot(JSON.parse(readFileSync(snapshotPath, 'utf8').replace(/^\uFEFF/, '')), Number(args.tolerance_mm ?? 0.01)),
      original_state_preserved: details.original_state_preserved === true,
    };
  } catch (error) {
    result.ok = false;
    result.error = {
      code: error instanceof AutomationFailure ? error.code : 'GEOMETRY_INVALID_INPUT',
      stage: 'calculating_geometry', message: error instanceof Error ? error.message : String(error),
    };
  }
  return result;
}
