import { McpServer, type CallToolResult } from '@modelcontextprotocol/server';
import { closeSync, fstatSync, openSync, readFileSync, readSync, statSync } from 'node:fs';
import { relative, isAbsolute } from 'node:path';
import { JobManager } from './jobs.js';
import { AutomationFailure, type Arguments } from './types.js';
import { getJobSchema, toolDefinitions } from './tools.js';

function response(data: object, failed = false): CallToolResult {
  return { content: [{ type: 'text', text: JSON.stringify(data) }], structuredContent: data as Record<string, unknown>, isError: failed };
}
function failure(error: unknown): CallToolResult {
  return response({ error: { code: error instanceof AutomationFailure ? error.code : 'INTERNAL_ERROR', message: error instanceof Error ? error.message : String(error) } }, true);
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
  } catch (error) { return { log_tail: [], log_error: error instanceof Error ? error.message : String(error) }; }
  finally { if (file !== undefined) closeSync(file); }
}
export function createServer(jobs: JobManager): McpServer {
  const server = new McpServer({ name: 'coreldraw', version: '0.1.0' }, { instructions: 'CorelDRAW 27 local automation. COM tools return job_id immediately. Poll corel_get_job, check status/result, and reuse request_id for identical retries. Never repeat an operation after timeout/unknown outcome. Document IDs last only for an executor session. execute_vba runs unrestricted local code.' });
  for (const [name, definition] of Object.entries(toolDefinitions)) {
    server.registerTool(name, {
      description: definition.description, inputSchema: definition.schema,
      annotations: { readOnlyHint: definition.readOnly, destructiveHint: definition.destructive ?? false, idempotentHint: !definition.readOnly, openWorldHint: name === 'corel_execute_vba' },
    }, async (args: Arguments) => {
      try { return response(jobs.submit(name, definition.schema.parse(args))); } catch (error) { return failure(error); }
    });
  }
  server.registerTool('corel_get_job', {
    description: 'Read a persisted job report without calling CorelDRAW, even when VBA is busy. Status is running, timed_out (still occupied), succeeded, failed, or unknown. Attach a completed preview PNG with include_preview=true.',
    inputSchema: getJobSchema,
    annotations: { readOnlyHint: true, idempotentHint: true, openWorldHint: false },
  }, async args => {
    try {
      const job = await jobs.wait(args.job_id, args.wait_seconds);
      const result = response({ ...job, ...logTail(job.log_path, args.log_lines) }, job.status === 'failed' || job.status === 'unknown');
      if (args.include_preview && job.status === 'succeeded' && job.tool === 'corel_preview') {
        const path = (job.result as { path?: string })?.path;
        if (path) {
          const rel = relative(job.run_directory, path);
          if (isAbsolute(rel) || rel === '..' || rel.startsWith('..\\') || rel.startsWith('../') || !job.artifacts.includes(path)) throw new Error('Preview is outside its run directory.');
          if (statSync(path).size > 10 * 1024 * 1024) throw new Error('Preview exceeds 10 MB.');
          result.content.push({ type: 'image', mimeType: 'image/png', data: readFileSync(path).toString('base64') });
        }
      }
      return result;
    } catch (error) { return failure(error); }
  });
  server.server.onclose = () => jobs.close();
  return server;
}
