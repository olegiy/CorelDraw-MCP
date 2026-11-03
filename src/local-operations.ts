import { mkdirSync } from 'node:fs';
import { join, resolve, relative, isAbsolute, sep } from 'node:path';
import { ArtifactStore, assertNoLinks, atomicJson, diagnosticSummary } from './artifacts.js';
import { JobManager } from './jobs.js';
import { AutomationFailure, type Arguments, type Job } from './types.js';

export class LocalOperations {
  readonly artifacts: ArtifactStore;
  constructor(private jobs: JobManager) {
    this.artifacts = new ArtifactStore(jobs.root, (id) => jobs.get(id));
  }
  run(tool: string, args: Arguments, job: Job): { result: unknown; error?: Job['error'] } {
    if (tool === 'corel_add_note') {
      if (typeof args.job_id === 'string') this.jobs.get(args.job_id);
      const directory = join(this.jobs.root, 'notes');
      assertNoLinks(directory);
      mkdirSync(directory, { recursive: true });
      const id = job.job_id;
      const path = join(directory, `${id}.json`);
      const note = {
        note_id: id,
        created_at: job.started_at,
        category: args.category,
        text: args.text,
        verified: false,
        ...(args.job_id ? { job_id: args.job_id } : {}),
      };
      atomicJson(path, note, false);
      job.artifacts.push(path);
      return { result: { note_id: id, path, verified: false } };
    }
    if (tool === 'corel_export_diagnostics') {
      const path = String(args.path);
      const rel = relative(resolve(this.jobs.root), resolve(path));
      if (!isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${sep}`))
        throw new AutomationFailure(
          'DIAGNOSTICS_PATH',
          'Отчёт для передачи сохраните вне каталога служебных заданий.',
        );
      const summary = diagnosticSummary(this.jobs.get(String(args.job_id)));
      atomicJson(path, summary, args.overwrite === true);
      job.artifacts.push(path);
      return { result: { path, diagnostic_id: summary.diagnostic_id } };
    }
    if (tool === 'corel_prune_artifacts') {
      const result = this.artifacts.prune(args.candidates as { job_id: string; prune_token: string }[]);
      return {
        result,
        ...(result.items.some((item) => !item.pruned)
          ? {
              error: {
                code: 'PRUNE_INCOMPLETE',
                stage: 'local',
                message:
                  'Не все выбранные артефакты удалены; проверьте коды результатов. Повтор всего задания не требуется.',
              },
            }
          : {}),
      };
    }
    throw new AutomationFailure('UNKNOWN_TOOL', 'Неизвестная локальная операция.');
  }
}
