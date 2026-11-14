"""Corel-specific jobs over the official Python MCP ClientSession. Never replay a send."""

import asyncio
import json
import re
import shutil
from pathlib import Path

from mcp import types

from .contracts import AgentError, ImageBlock, JobUpdate, OperationResult, ToolDefinition
from .owned_stdio import OwnedStdio

ROOT = Path(__file__).resolve().parents[2]
JOB_ID = re.compile(r"^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$")


class CorelMcp:
    def __init__(self, *, transport=None, session=None, directory=None, root=ROOT, poll_interval=0.25):
        self.transport, self.session = transport, session
        self.directory, self.root = directory, Path(root)
        self.poll_interval = max(0.01, poll_interval)
        self.ready = session is not None
        self.guide = ""
        self.catalog = ()
        self._lane = asyncio.Lock()
        self._inflight = set()
        self._closing = False
        self.persistence_failed = False

    async def start(self):
        for package in ("server", "client"):
            path = self.root / f"node_modules/@modelcontextprotocol/{package}/package.json"
            data = await asyncio.to_thread(lambda: json.loads(path.read_text(encoding="utf-8")))
            if data.get("version") != "2.4.0":
                raise AgentError("NODE_SDK_UNSUPPORTED", "Проверен только Node MCP SDK 2.4.0.", 503)
        node = shutil.which("node")
        if not node:
            raise AgentError("NODE_UNAVAILABLE", "Нужен Node.js 24.", 503)
        check = await asyncio.create_subprocess_exec(node, "--version", stdout=asyncio.subprocess.PIPE,
                                                     stderr=asyncio.subprocess.DEVNULL)
        stdout, _ = await check.communicate()
        if check.returncode or not stdout.decode().strip().startswith("v24."):
            raise AgentError("NODE_UNSUPPORTED", "Нужен Node.js 24.", 503)
        if self.transport is None and self.session is None:
            self.transport = OwnedStdio(command=node, args=[str(self.root / "dist/index.js")],
                                         directory=self.directory, cwd=str(self.root))
        if self.transport:
            if self.transport.session is None:
                await self.transport.start()
            self.session = self.transport.session
        cursor, seen, catalog = None, set(), []
        for _ in range(32):
            page = await self.session.list_tools(params=types.PaginatedRequestParams(cursor=cursor))
            for tool in page.tools:
                if tool.name in {item.name for item in catalog}:
                    raise AgentError("MCP_CATALOG_INVALID", "Повтор имени инструмента MCP.", 503)
                catalog.append(ToolDefinition(name=tool.name, description=tool.description or "",
                    input_schema=tool.inputSchema,
                    annotations=tool.annotations.model_dump(exclude_none=True) if tool.annotations else {}))
            cursor = page.nextCursor
            if cursor is None:
                break
            if cursor in seen:
                raise AgentError("MCP_CATALOG_INVALID", "Повтор cursor в каталоге MCP.", 503)
            seen.add(cursor)
        else:
            raise AgentError("MCP_CATALOG_INVALID", "Каталог MCP превышает лимит страниц.", 503)
        names = {tool.name for tool in catalog}
        if not {"corel_get_job", "corel_guide", "corel_status"} <= names:
            raise AgentError("MCP_INCOMPATIBLE", "MCP не предоставляет обязательный Job/guide контракт.", 503)
        self.catalog = tuple(catalog)
        pages = []
        for topic in ("tools", "pitfalls"):
            offset = 0
            for _ in range(64):
                result = await self.session.call_tool("corel_guide", {"topic": topic, "offset": offset, "limit": 8000})
                report = self._report(result)
                if result.isError or not isinstance(report.get("text"), str):
                    raise AgentError("GUIDE_UNAVAILABLE", "Не удалось загрузить доверенное руководство Corel MCP.", 503)
                pages.append(report["text"])
                next_offset = report.get("next_offset")
                if next_offset is None:
                    break
                if not isinstance(next_offset, int) or next_offset <= offset:
                    raise AgentError("GUIDE_INVALID", "Некорректная пагинация руководства.", 503)
                offset = next_offset
            else:
                raise AgentError("GUIDE_INVALID", "Руководство превышает лимит.", 503)
        self.guide = "\n".join(pages)
        self.ready = True

    async def tools(self):
        return list(self.catalog)

    @staticmethod
    def _report(result):
        if not isinstance(result.structuredContent, dict):
            raise AgentError("MCP_REPORT_INVALID", "Нет структурированного Corel-отчёта.", 503)
        return result.structuredContent

    def _result(self, operation, report, *, images=(), is_error=False):
        applied = report.get("error", {}).get("applied")
        if not isinstance(applied, bool):
            value = report.get("result")
            applied = value.get("applied") if isinstance(value, dict) else None
        if not isinstance(applied, bool):
            applied = None
        status = report.get("status")
        outcome = status if status in {"succeeded", "failed", "unknown"} else "failed"
        return OperationResult(operation_id=operation.operation_id, job_id=report.get("job_id"),
            outcome=outcome, report=report, applied=applied, cleanup=report.get("cleanup"),
            images=images, isError=is_error)

    async def _emit(self, operation, report, callback, monitoring="healthy", is_error=False):
        try:
            await callback(JobUpdate(operation_id=operation.operation_id, job_id=report["job_id"],
                status=report["status"], stage=report.get("stage", "unknown"), report=report,
                monitoring_state=monitoring, isError=is_error))
        except Exception:
            # A failed ledger write must not cancel or repeat an already sent COM operation.
            self.persistence_failed = True

    async def _wait(self, operation, report, callback, *, response=None):
        job_id = report["job_id"]
        monitoring, delay = "healthy", self.poll_interval
        await self._emit(operation, report, callback, is_error=bool(response and response.isError))
        preview = operation.tool == "corel_preview"
        while report["status"] in {"running", "timed_out"} or (preview and report["status"] == "succeeded"):
            terminal_preview = preview and report["status"] == "succeeded"
            try:
                response = await self.session.call_tool("corel_get_job", {
                    "job_id": job_id, "wait_seconds": 2, "log_lines": 50, "include_preview": preview,
                })
                new_report = self._report(response)
                if new_report.get("job_id") != job_id or new_report.get("status") not in {
                    "running", "timed_out", "succeeded", "failed", "unknown",
                }:
                    raise AgentError("MCP_REPORT_INVALID", "Опрос вернул несовместимый Job.", 503)
                changed = new_report != report or monitoring != "healthy"
                report, monitoring, delay = new_report, "healthy", self.poll_interval
                if changed:
                    await self._emit(operation, report, callback, is_error=response.isError)
            except Exception:
                if self.transport is not None and self.transport.returncode is not None:
                    self.ready = False
                    exited = {**report, "status": "unknown", "stage": "transport_exited",
                        "error": {"code": "MCP_PROCESS_EXITED", "message": "Owned MCP завершился до terminal report; фактический Job сохранён, повтор запрещён."}}
                    await self._emit(operation, exited, callback, "degraded", is_error=True)
                    return OperationResult(operation_id=operation.operation_id, job_id=job_id,
                        outcome="unknown", report=exited, applied=None, cleanup=None, isError=True)
                if terminal_preview:
                    return self._result(operation, report, is_error=True)
                monitoring = "degraded"
                await self._emit(operation, report, callback, monitoring)
                await asyncio.sleep(delay)
                delay = min(5.0, max(self.poll_interval, delay * 2))
                continue
            if report["status"] not in {"running", "timed_out"}:
                images = ()
                if preview and report["status"] == "succeeded":
                    blocks = [block for block in response.content if isinstance(block, types.ImageContent)]
                    if len(blocks) == 1:
                        images = (ImageBlock(mime_type=blocks[0].mimeType, data=blocks[0].data),)
                return self._result(operation, report, images=images, is_error=response.isError)
            await asyncio.sleep(delay)
        return self._result(operation, report, is_error=bool(response and response.isError))

    async def _call(self, operation, callback, known_job=None):
        if self._lane.locked():
            raise AgentError("BUSY", "MCP-lane занята; скрытая параллельная операция запрещена.")
        async with self._lane:
            if self.persistence_failed and known_job is None:
                raise AgentError("PERSISTENCE_FAILED", "Сохранение результата не подтверждено; новый dispatch запрещён.", 503)
            if not self.ready or self._closing:
                raise AgentError("MCP_UNAVAILABLE", "Owned MCP не готов.", 503)
            if known_job:
                report = {"job_id": known_job, "status": "running", "stage": "recovery"}
                return await self._wait(operation, report, callback)
            try:
                response = await self.session.call_tool(operation.tool, operation.arguments)
                report = self._report(response)
                if not report.get("job_id"):
                    if response.isError and isinstance(report.get("error"), dict):
                        return self._result(operation, report, is_error=True)
                    raise AgentError("MCP_REPORT_INVALID", "Нет фактического job_id.", 503)
                if not JOB_ID.fullmatch(report["job_id"]):
                    raise AgentError("MCP_REPORT_INVALID", "Неверный job_id.", 503)
                if report.get("status") not in {"running", "timed_out", "succeeded", "failed", "unknown"}:
                    raise AgentError("MCP_REPORT_INVALID", "Несовместимый статус Job.", 503)
                if report.get("tool") != operation.tool or (operation.corel_request_id is not None and
                        report.get("request_id") != operation.corel_request_id):
                    raise AgentError("MCP_REPORT_INVALID", "Привязка Job не совпадает с отправленной операцией.", 503)
            except Exception:
                return OperationResult(operation_id=operation.operation_id, outcome="unknown", isError=True,
                    report={"status": "unknown", "error": {"code": "DISPATCH_OUTCOME_UNKNOWN",
                    "stage": "dispatching", "message": "Связь потеряна до получения job_id; повтор запрещён."}})
            return await self._wait(operation, report, callback, response=response)

    async def call_job(self, operation, emit_job_update):
        task = asyncio.create_task(self._call(operation, emit_job_update))
        self._inflight.add(task)
        task.add_done_callback(self._inflight.discard)
        return await asyncio.shield(task)

    async def poll_known(self, operation, job_id, emit_job_update):
        if not JOB_ID.fullmatch(job_id):
            raise AgentError("JOB_BINDING_INVALID", "Нужен сохранённый фактический job_id.", 503)
        task = asyncio.create_task(self._call(operation, emit_job_update, job_id))
        self._inflight.add(task)
        task.add_done_callback(self._inflight.discard)
        return await asyncio.shield(task)

    async def aclose(self):
        if not hasattr(self, "_shutdown_task"):
            self._shutdown_task = asyncio.create_task(self._shutdown())
        cancelled = False
        while not self._shutdown_task.done():
            try:
                await asyncio.shield(self._shutdown_task)
            except asyncio.CancelledError:
                cancelled = True
        self._shutdown_task.result()
        if cancelled:
            raise asyncio.CancelledError

    async def _shutdown(self):
        # Drain first; setting closing before drain would reject a queued known operation.
        if self._inflight:
            await asyncio.shield(asyncio.gather(*self._inflight, return_exceptions=True))
        self._closing, self.ready = True, False
        if self.transport:
            await self.transport.aclose()
