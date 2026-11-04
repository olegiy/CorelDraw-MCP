import { McpServer, type CallToolResult } from '@modelcontextprotocol/server';
import { closeSync, fstatSync, openSync, readFileSync, readSync, statSync } from 'node:fs';
import { relative, isAbsolute } from 'node:path';
import { JobManager } from './jobs.js';
import { AutomationFailure, type Arguments } from './types.js';
import { getJobSchema, guideSchema, listArtifactsSchema, toolDefinitions } from './tools.js';
import { guide, guideTopics, readGuide } from './guides.js';
import { LocalOperations } from './local-operations.js';
import { VERSION } from './version.js';
import { assertNoLinks } from './artifacts.js';
import type { ToolPolicy } from './config.js';

function response(data: object, failed = false): CallToolResult {
  return {
    content: [{ type: 'text', text: JSON.stringify(data) }],
    structuredContent: data as Record<string, unknown>,
    isError: failed,
  };
}
function failure(error: unknown): CallToolResult {
  return response(
    {
      error: {
        code: error instanceof AutomationFailure ? error.code : 'INTERNAL_ERROR',
        message: error instanceof Error ? error.message : String(error),
      },
    },
    true,
  );
}
function logTail(path: string, limit: number): object {
  if (!limit) return {};
  let file;
  try {
    file = openSync(path, 'r');
    const size = fstatSync(file).size;
    const offset = Math.max(0, size - 65536);
    const buffer = Buffer.alloc(size - offset);
    readSync(file, buffer, 0, buffer.length, offset);
    const lines = buffer.toString('utf8').trimEnd().split('\n');
    if (offset > 0) lines.shift(); // Omit a partial line/UTF-8 character at the boundary.
    return { log_tail: lines.slice(-limit), log_truncated: offset > 0 || lines.length > limit };
  } catch (error) {
    return { log_tail: [], log_error: error instanceof Error ? error.message : String(error) };
  } finally {
    if (file !== undefined) closeSync(file);
  }
}
function safeJobLog(job: { log_path: string; run_directory: string }, limit: number): object {
  const rel = relative(job.run_directory, job.log_path);
  if (isAbsolute(rel) || rel === '..' || rel.startsWith('..\\') || rel.startsWith('../'))
    return { log_tail: [], log_error: 'Журнал находится вне каталога задания.' };
  try {
    assertNoLinks(job.log_path);
  } catch (error) {
    return { log_tail: [], log_error: error instanceof Error ? error.message : String(error) };
  }
  return logTail(job.log_path, limit);
}
export function createServer(jobs: JobManager, policy?: ToolPolicy): McpServer {
  const local = new LocalOperations(jobs);
  const server = new McpServer(
    { name: 'coreldraw', version: VERSION },
    {
      instructions:
        'Локальная автоматизация CorelDRAW 27. Прочитайте corel_guide. COM-операции возвращают job_id: опрашивайте corel_get_job. После timed_out/unknown повтор запрещён. succeeded с cleanup.completed=false означает выполненное действие с проблемой восстановления; оно не требует повтора. VBA имеет полный доступ к системе.',
    },
  );
  for (const [name, definition] of Object.entries(toolDefinitions)) {
    if (name === 'corel_list_artifacts') continue;
    if (policy && !policy.isEnabled(name)) continue;
    server.registerTool(
      name,
      {
        description: definition.description,
        inputSchema: definition.schema,
        annotations: {
          readOnlyHint: definition.readOnly,
          destructiveHint: definition.destructive ?? false,
          idempotentHint: !definition.readOnly,
          openWorldHint: name === 'corel_execute_vba',
        },
      },
      async (args: Arguments) => {
        try {
          const parsed = definition.schema.parse(args);
          const job = definition.local
            ? jobs.submitLocal(name, parsed, (accepted) => local.run(name, parsed, accepted))
            : jobs.submit(name, parsed);
          return response(
            job,
            job.status === 'failed' || job.status === 'unknown' || job.cleanup?.completed === false,
          );
        } catch (error) {
          return failure(error);
        }
      },
    );
  }
  server.registerTool(
    'corel_get_job',
    {
      description:
        'Прочитать сохранённый отчёт задания без вызова CorelDRAW, даже если VBA занят. Статусы: running, timed_out (исполнитель ещё занят), succeeded, failed или unknown. Для готового PNG-превью укажите include_preview=true.',
      inputSchema: getJobSchema,
      annotations: { readOnlyHint: true, idempotentHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        const job = await jobs.wait(args.job_id, args.wait_seconds);
        const result = response(
          { ...job, ...(!job.details_pruned ? safeJobLog(job, args.log_lines) : {}) },
          job.status === 'failed' || job.status === 'unknown' || job.cleanup?.completed === false,
        );
        if (args.include_preview && job.status === 'succeeded' && job.tool === 'corel_preview') {
          const path = (job.result as { path?: string })?.path;
          if (path) {
            const rel = relative(job.run_directory, path);
            if (
              isAbsolute(rel) ||
              rel === '..' ||
              rel.startsWith('..\\') ||
              rel.startsWith('../') ||
              !job.artifacts.includes(path)
            )
              throw new Error('Превью находится вне каталога задания.');
            assertNoLinks(path);
            if (statSync(path).size > 10 * 1024 * 1024) throw new Error('Превью превышает 10 МиБ.');
            result.content.push({
              type: 'image',
              mimeType: 'image/png',
              data: readFileSync(path).toString('base64'),
            });
          }
        }
        return result;
      } catch (error) {
        return failure(error);
      }
    },
  );
  server.registerTool(
    'corel_guide',
    {
      description:
        'Прочитать единую локальную справку без COM. Без темы — оглавление; offset/limit ограничивают текст.',
      inputSchema: guideSchema,
      annotations: { readOnlyHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        return response(guide(args.topic, args.offset, args.limit));
      } catch (error) {
        return failure(error);
      }
    },
  );
  server.registerTool(
    'corel_list_artifacts',
    {
      description: 'Размеры, возраст, защита и prune_token для ручной очистки. Без COM.',
      inputSchema: listArtifactsSchema,
      annotations: { readOnlyHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        return response(local.artifacts.list(args.offset, args.limit));
      } catch (error) {
        return failure(error);
      }
    },
  );
  for (const entry of guideTopics)
    server.registerResource(
      entry.topic,
      entry.uri,
      { title: entry.title, mimeType: 'text/markdown' },
      async (uri) => ({
        contents: [{ uri: uri.href, mimeType: 'text/markdown', text: readGuide(entry.topic) }],
      }),
    );
  server.server.onclose = () => jobs.close();
  return server;
}
