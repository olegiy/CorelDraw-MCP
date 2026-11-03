# MCP tools and job results

All application tools return a job immediately. Read the report with `corel_get_job(job_id, wait_seconds=20)`; `include_preview=true` attaches a finished PNG preview. Reports are persisted even when the client disconnects while a COM call is running.

`corel_get_job` includes the last 50 log lines by default; use `log_lines=0` to omit them or up to 200 (bounded to 64 KiB). `log_path` identifies the complete local log. Status reports include executor process/STA details and the current optimization/events flags.

| Tool | Required inputs | Optional inputs / defaults |
| --- | --- | --- |
| corel_status | none | deadline_seconds=600 |
| corel_inspect_document | document_id | page_index=current, offset=0, limit=100 (max 500) |
| corel_create_document | request_id | width_mm=210, height_mm=297 |
| corel_open_document | request_id, path | existing .cdr, returns same ID if already open |
| corel_save_document | request_id, document_id | path=current, overwrite=false |
| corel_close_document | request_id, document_id | discard_changes=false |
| corel_export | request_id, document_id, path, format | range=page, page_index=current, overwrite=false, dpi=150 |
| corel_preview | document_id | page_index=current, max_size=1200 (64..2400) |
| corel_build_vba | request_id, source_root | module_paths=all root .bas |
| corel_execute_vba | request_id, document_id, code | Main() As String returns JSON |

All application operations accept `deadline_seconds` (1..3600). The deadline is observational, not cancellation. Polling a timed_out job returns immediately; space further checks reasonably while the user resolves the application state.

Inspection limits each array (shapes, pages, layers, selection_ids) to `limit` entries. Shape pagination uses offset/next_offset; metadata includes total counts and truncation flags. Use a higher limit or targeted VBA to inspect further metadata.

Paths must be absolute Windows paths. File parents must already exist. Save/export extensions must match CDR or the selected format. Saving back to a document's own path is allowed; replacing another file requires overwrite=true. A path open in another document cannot be overwritten as CDR. An unsaved document needs a save path. Dirty documents cannot close without save or explicit discard.

PNG/SVG support page or selection; PDF supports page or document. Selection export requires selected objects on the current page. PNG export is limited to 40 megapixels. Preview renders a full page and writes inside the job directory. PDF/SVG use application filter defaults; detailed prepress settings can be set through task-specific VBA.

Build accepts 1..100 standard .bas modules, up to 2 MiB in total. `module_paths` are relative to source_root and cannot traverse junctions below that root. Module names must be unique ASCII VBA names of at most 31 characters. Files are UTF-8; Attribute VB_* lines are removed only in the loaded snapshot. Forms/classes are not supported. Execute accepts up to 256 KiB of source; the returned JSON is limited to 1 MiB.

Every report includes run_directory, report_path, log_path and artifacts. Errors include code, stage, number and source where available; compiler_location is populated only for an actual selected compiler error span. A normal editor caret is not a diagnostic. cleanup.completed=false requires attention even if the requested action succeeded.

The default artifact directory is `%LOCALAPPDATA%\CorelDrawMcp\runs`. For unknown outcomes inspect request.json, run.log, report.json and the retained project. Restarting the executor invalidates document IDs and does not undo geometry or file changes. The server never automatically repeats timed-out or orphaned requests.
