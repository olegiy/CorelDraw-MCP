import * as z from 'zod';
import { win32 } from 'node:path';
import type { Arguments } from './types.js';

const request = {
  request_id: z.string().min(1).max(200).describe('A stable identifier for this logical operation. Reuse only with identical arguments when retrying.'),
};
const deadline = { deadline_seconds: z.number().int().min(1).max(3600).default(600).describe('Deadline in seconds. Expiry does not cancel a COM call; the executor remains occupied until it finishes.') };
const document = { document_id: z.string().min(1).max(100).describe('Opaque document_id returned by this executor session; never infer it from a filename or index.') };
const absolutePath = z.string().min(1).max(1024).refine(value => win32.isAbsolute(value) && !value.includes('\0'), 'Use an absolute Windows path, for example C:\\Work\\drawing.cdr.');
const pageIndex = z.number().int().min(1).describe('One-based page index in the selected document. Defaults to its current page when omitted.');
interface Definition {
  schema: z.ZodType<Arguments>;
  description: string;
  readOnly: boolean;
  destructive?: boolean;
}
export const toolDefinitions: Record<string, Definition> = {
  corel_status: {
    schema: z.strictObject({ ...deadline }), readOnly: true,
    description: 'Connect to CorelDRAW 27 and report its version, VBA availability/mode, executor state, optimization/events and open document IDs. May start CorelDRAW. Returns a job_id; use corel_get_job for the result.',
  },
  corel_inspect_document: {
    schema: z.strictObject({ ...document, page_index: pageIndex.optional(), offset: z.number().int().min(0).default(0).describe('Zero-based offset into top-level page shapes.'), limit: z.number().int().min(1).max(500).default(100).describe('Maximum entries per array: shapes, pages, layers and selection IDs. Metadata arrays include totals and truncation flags.'), ...deadline }), readOnly: true,
    description: 'Inspect one document: page metadata, layers, selection, and a bounded slice of top-level shapes with StaticID, type and bounds in the document unit. Includes total counts and truncation flags; use VBA for further metadata or nested shapes. Returns a job_id.',
  },
  corel_create_document: {
    schema: z.strictObject({ ...request, width_mm: z.number().positive().max(100000).default(210).describe('Initial page width in millimetres.'), height_mm: z.number().positive().max(100000).default(297).describe('Initial page height in millimetres.'), ...deadline }), readOnly: false,
    description: 'Create an unsaved CorelDRAW document with one page and millimetre units. Returns a job_id whose result contains the new document_id.',
  },
  corel_open_document: {
    schema: z.strictObject({ ...request, path: absolutePath.describe('Existing .cdr file to open. An already open file is returned without opening it twice.'), ...deadline }), readOnly: false,
    description: 'Open a CDR file and register its document_id. Does not save or close other drawings. Returns a job_id.',
  },
  corel_save_document: {
    schema: z.strictObject({ ...request, ...document, path: absolutePath.optional().describe('Destination .cdr path. Omit to save to the document current path; unsaved documents require a path.'), overwrite: z.boolean().default(false).describe('Allow replacing a different existing file. Saving back to the same document path needs no overwrite flag.'), ...deadline }), readOnly: false, destructive: true,
    description: 'Explicitly save a document as CDR. Refuses to replace a different existing file unless overwrite=true and refuses a destination open in another document. Returns a job_id.',
  },
  corel_close_document: {
    schema: z.strictObject({ ...request, ...document, discard_changes: z.boolean().default(false).describe('Explicitly discard unsaved edits. If false, a dirty document is refused; save it first.'), ...deadline }), readOnly: false, destructive: true,
    description: 'Close a specific document and invalidate its ID. Unsaved edits require prior save or discard_changes=true. Does not quit CorelDRAW. Returns a job_id.',
  },
  corel_export: {
    schema: z.strictObject({ ...request, ...document, path: absolutePath.describe('Destination path with extension matching format; its parent directory must exist.'), format: z.enum(['png', 'pdf', 'svg']).describe('Export file format.'), range: z.enum(['page', 'selection', 'document']).default('page').describe('PNG/SVG support page or selection; PDF supports page or document.'), page_index: pageIndex.optional(), overwrite: z.boolean().default(false).describe('Allow replacing an existing export file.'), dpi: z.number().int().min(1).max(1200).default(150).describe('PNG resolution in dpi; ignored for PDF/SVG. PNG output is limited to 40 megapixels.'), ...deadline }), readOnly: false, destructive: true,
    description: 'Export a selected drawing to PNG/SVG or PDF using CorelDRAW filters. Exports to an intermediate file and commits only after success. Does not save the CDR. Returns a job_id with file path and byte size.',
  },
  corel_preview: {
    schema: z.strictObject({ ...document, page_index: pageIndex.optional(), max_size: z.number().int().min(64).max(2400).default(1200).describe('Maximum width or height of the PNG preview in pixels.'), ...deadline }), readOnly: true,
    description: 'Render one document page as a bounded RGB PNG inside the job directory. Poll corel_get_job with include_preview=true to see the image. Returns a job_id.',
  },
  corel_build_vba: {
    schema: z.strictObject({ ...request, source_root: absolutePath.describe('Directory containing UTF-8 standard VBA .bas modules.'), module_paths: z.array(z.string().min(1).max(1024)).min(1).max(100).optional().describe('Optional relative .bas paths inside source_root, including subdirectories. By default all root .bas files are loaded.'), ...deadline }), readOnly: false,
    description: 'Snapshot and fully compile standard .bas modules in a unique GMS via VBE command 578. Never runs a macro. Successful projects unload; failed projects remain loaded for diagnosis. Returns a job_id.',
  },
  corel_execute_vba: {
    schema: z.strictObject({ ...request, ...document, code: z.string().min(1).max(262144).describe('A complete UTF-8 standard VBA module declaring Public Function Main() As String. Main must return valid JSON. The target document is activated before invocation.'), ...deadline }), readOnly: false, destructive: true,
    description: 'Compile and execute arbitrary VBA in a unique GMS against the chosen document, with an outer Undo group and restoration of optimization/events. Code has full CorelDRAW and filesystem access: this is NOT a sandbox. Avoid modal dialogs, Shell and application shutdown. Return JSON describing verifiable results. Returns a job_id.',
  },
};
export const getJobSchema = z.strictObject({
  job_id: z.string().min(1).max(100).describe('Exact job_id returned by an operation.'),
  wait_seconds: z.number().min(0).max(20).default(0).describe('Wait up to this many seconds for a running job. timed_out jobs return immediately.'),
  log_lines: z.number().int().min(0).max(200).default(50).describe('Include this many recent log lines (at most 64 KiB). Set 0 to omit log text.'),
  include_preview: z.boolean().default(false).describe('Attach the PNG image when a preview job has succeeded.'),
});
