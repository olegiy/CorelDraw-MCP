"""Thin API. No endpoint can start a model/MCP task except durable runtime commands."""

import asyncio
import os
import re
from contextlib import asynccontextmanager
from pathlib import Path
from urllib.parse import quote

from fastapi import FastAPI, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response
from sse_starlette import EventSourceResponse

from .access import COOKIE_NAME, MAX_BODY_BYTES, SECURITY_HEADERS
from .contracts import (
    TERMINAL_STATUSES,
    AgentError,
    BackupResult,
    ConfirmCommand,
    Conversation,
    ConversationCreate,
    ConversationPage,
    DiagnosticsSummary,
    DocumentsCache,
    EmptyCommand,
    ErrorResponse,
    EventPage,
    FileRegister,
    Health,
    Id,
    MessagePage,
    OperationPage,
    ProfilePage,
    ProfileSnapshot,
    ProfileTestResult,
    ProfileUpdate,
    PublicFileRef,
    ReconcileResult,
    RecoveryReview,
    RecoveryReviewPage,
    ReportView,
    RunCommand,
    RunPage,
    RunSnapshot,
    SessionResponse,
    ToolDefinition,
    Workspace,
    new_id,
)
from .events import run_stream
from .policy import PathGuard
from .provider import Provider


def error_response(error, trace_id):
    return JSONResponse(ErrorResponse(code=error.code, message=error.message,
                        trace_id=trace_id, retryable=error.retryable).model_dump(), status_code=error.status)


class LocalBoundary:
    def __init__(self, app, access=None):
        self.app, self.access = app, access

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        scope.setdefault("state", {})["trace_id"] = new_id()
        async def safe_send(message):
            if message["type"] == "http.response.start":
                names = {key for key, _ in SECURITY_HEADERS}
                message["headers"] = [*(item for item in message["headers"] if item[0].lower() not in names),
                                      *SECURITY_HEADERS]
            await send(message)
        try:
            request = Request(scope, receive)
            for key in ('offset', 'after', 'limit'):
                values = request.query_params.getlist(key)
                if values and (len(values) != 1 or re.fullmatch(r'[0-9]{1,18}', values[0]) is None):
                    raise AgentError('SCHEMA_INVALID', 'Нужен ограниченный целочисленный cursor без дроби или экспоненты.', 422)
            if self.access is not None:
                await self.access.check(request)
            elif scope["path"].startswith("/api/"):
                raise AgentError("SESSION_REQUIRED", "Откройте панель через локальный launcher.", 401)
            if request.method not in {"GET", "HEAD", "OPTIONS"}:
                lengths = request.headers.getlist("content-length")
                if lengths and (len(lengths) != 1 or len(lengths[0]) > 12 or not lengths[0].isdecimal() or int(lengths[0]) > MAX_BODY_BYTES):
                    raise AgentError("BODY_TOO_LARGE", "Тело запроса превышает 2 МиБ или имеет неверную длину.", 413)
                body = bytearray()
                while True:
                    message = await receive()
                    if message["type"] == "http.disconnect":
                        return
                    chunk = message.get("body", b"")
                    if len(body) + len(chunk) > MAX_BODY_BYTES:
                        raise AgentError("BODY_TOO_LARGE", "Фактически прочитанное тело превышает 2 МиБ.", 413)
                    body.extend(chunk)
                    if not message.get("more_body", False):
                        break
                delivered = False
                async def buffered_receive():
                    nonlocal delivered
                    if not delivered:
                        delivered = True
                        return {"type": "http.request", "body": bytes(body), "more_body": False}
                    return await receive()
                await self.app(scope, buffered_receive, safe_send)
            else:
                await self.app(scope, receive, safe_send)
        except AgentError as error:
            await error_response(error, scope["state"]["trace_id"])(scope, receive, safe_send)


def create_app(*, runtime=None, store=None, assets=None, access=None, lifespan=None):
    @asynccontextmanager
    async def owned_lifespan(app):
        try:
            yield
        finally:
            if runtime:
                await runtime.aclose()

    app = FastAPI(title="CorelDRAW Agent", version="1.0.0", docs_url=None,
                  redoc_url=None, openapi_url=None, lifespan=lifespan or owned_lifespan,
                  telemetry={"tracing": False, "metrics": False, "logs": False},
                  responses={code: {"model": ErrorResponse} for code in (401, 403, 404, 409, 413, 422, 503)})
    app.add_middleware(LocalBoundary, access=access)
    app.state.runtime, app.state.store, app.state.assets, app.state.access = runtime, store, assets, access

    @app.exception_handler(AgentError)
    async def agent_error(request, error):
        return error_response(error, request.state.trace_id)

    @app.exception_handler(RequestValidationError)
    async def invalid_request(request, error):
        return error_response(AgentError("SCHEMA_INVALID", "Неверная схема запроса; значения не включены в диагностику.", 422), request.state.trace_id)

    @app.exception_handler(Exception)
    async def internal_error(request, error):
        return error_response(AgentError("INTERNAL_ERROR", "Внутренняя ошибка; новые действия не подтверждены.", 503), request.state.trace_id)

    @app.post("/api/v1/access/bootstrap", response_model=SessionResponse)
    async def bootstrap(request: Request, response: Response):
        cookie, session = access.exchange(request.headers.get("x-corel-bootstrap"))
        response.set_cookie(COOKIE_NAME, cookie, httponly=True, samesite="strict", path="/api/v1", secure=False)
        return session

    @app.get("/api/v1/access/session", response_model=SessionResponse)
    async def session(request: Request):
        return access.session_response(request)

    @app.post("/api/v1/conversations", response_model=Conversation, status_code=201)
    async def create_conversation(command: ConversationCreate):
        return await store.create_conversation(command.title)

    @app.get("/api/v1/conversations", response_model=ConversationPage)
    async def conversations(offset: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500)):
        items = await store.list_conversations(offset=offset, limit=limit)
        return ConversationPage(items=items, next_offset=offset + limit if len(items) == limit else None)

    @app.post("/api/v1/history/backup", response_model=BackupResult, status_code=201)
    async def history_backup(command: EmptyCommand):
        await store.backup()
        return BackupResult()

    @app.get("/api/v1/conversations/{conversation_id}/messages", response_model=MessagePage)
    async def messages(conversation_id: Id, offset: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500)):
        items = await store.get_messages(conversation_id, offset=offset, limit=limit)
        return MessagePage(items=items, next_offset=offset + limit if len(items) == limit else None)

    @app.get("/api/v1/profiles", response_model=ProfilePage)
    async def profiles():
        if not runtime.settings:
            raise AgentError("SETTINGS_UNAVAILABLE", "Хранилище профиля не подключено.", 503)
        return ProfilePage(items=await runtime.settings.list_profiles())

    @app.put("/api/v1/profiles/{profile_id}", response_model=ProfileSnapshot)
    async def save_profile(profile_id: str, command: ProfileUpdate):
        if profile_id != command.profile.id:
            raise AgentError("PROFILE_ID_CONFLICT", "ID профиля в URL и теле не совпадают.")
        if not runtime.settings:
            raise AgentError("SETTINGS_UNAVAILABLE", "Хранилище профиля не подключено.", 503)
        return await runtime.settings.save(command.profile, command.api_key)

    @app.post("/api/v1/profiles/{profile_id}/test", response_model=ProfileTestResult)
    async def test_profile(profile_id: str):
        if not runtime.settings:
            raise AgentError("SETTINGS_UNAVAILABLE", "Хранилище профиля не подключено.", 503)
        frozen = await runtime.settings.freeze(profile_id, require_consent=False)
        provider = Provider(frozen.profile, frozen.secret)
        try:
            tool = ToolDefinition(name="corel_agent_probe", description="Проверка native tools без CorelDRAW.",
                input_schema={"type": "object", "properties": {"ok": {"type": "boolean", "const": True}},
                              "required": ["ok"], "additionalProperties": False})
            completion = await provider.complete([{"role": "user", "content":
                "Вызови corel_agent_probe с ok=true. Это тест API без пользовательских документов."}], [tool])
            if (len(completion.tool_calls) != 1 or completion.tool_calls[0].name != tool.name
                    or completion.tool_calls[0].arguments != {"ok": True}):
                raise AgentError("NATIVE_TOOLS_REQUIRED", "Модель не вернула обязательный native tool call.", 502)
            return ProfileTestResult()
        finally:
            await provider.aclose()

    @app.get("/api/v1/workspace", response_model=Workspace)
    async def workspace():
        return await store.get_meta("workspace", Workspace().model_dump())

    @app.put("/api/v1/workspace", response_model=Workspace)
    async def set_workspace(command: Workspace):
        if len({root.id for root in command.output_roots}) != len(command.output_roots):
            raise AgentError("ROOT_ID_CONFLICT", "ID output roots должны быть уникальны.", 422)
        roots = []
        for root in command.output_roots:
            path = await asyncio.to_thread(PathGuard().directory, root.path)
            roots.append(root.model_copy(update={"path": str(path)}))
        result = Workspace(output_roots=roots)
        await store.put_meta("workspace", result.model_dump(mode="json"))
        return result

    @app.post("/api/v1/files/register", response_model=PublicFileRef, status_code=201)
    async def register_file(command: FileRegister):
        reference = await PathGuard().register_file(command.path)
        async with runtime._lock:
            files = await store.get_meta("files", [])
            if len(files) >= 100:
                raise AgentError("FILE_REF_LIMIT", "Достигнут лимит зарегистрированных CDR.")
            await store.put_meta("files", [*files, reference.model_dump(mode="json")])
        return PublicFileRef(file_ref=reference.file_ref, display_name=reference.display_name)

    @app.post("/api/v1/actions", response_model=RunSnapshot, status_code=202)
    async def action(command: RunCommand):
        return await runtime.start(command.model_copy(update={"kind": "manual"}))

    @app.post("/api/v1/runs", response_model=RunSnapshot, status_code=202)
    async def agent_run(command: RunCommand):
        return await runtime.start(command.model_copy(update={"kind": "agent"}))

    @app.get("/api/v1/documents", response_model=DocumentsCache)
    async def documents():
        return await store.get_meta("documents", DocumentsCache().model_dump())

    @app.post("/api/v1/documents/refresh", response_model=RunSnapshot, status_code=202)
    async def refresh_documents(command: RunCommand):
        return await runtime.start(command.model_copy(update={"kind": "manual", "tool": "corel_status",
            "arguments": {}, "read_only": True, "document_id": None, "profile_id": None}))

    @app.post("/api/v1/diagnostics", response_model=RunSnapshot, status_code=202)
    async def diagnostics_run(command: RunCommand):
        return await runtime.start(command.model_copy(update={"kind": "manual", "tool": "host_diagnostics",
            "arguments": {}, "read_only": True, "document_id": None, "profile_id": None}))

    @app.get("/api/v1/runs", response_model=RunPage)
    async def runs(offset: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500), conversation_id: Id | None = None):
        items = await store.list_runs(offset=offset, limit=limit, conversation_id=conversation_id)
        return RunPage(items=items, next_offset=offset + limit if len(items) == limit else None)

    @app.get("/api/v1/runs/{run_id}", response_model=RunSnapshot)
    async def snapshot(run_id: Id):
        return await store.get_snapshot(run_id)

    @app.get("/api/v1/requests/{client_request_id}", response_model=RunSnapshot)
    async def request_snapshot(client_request_id: Id):
        snapshot = await store.get_request(client_request_id)
        if snapshot is None:
            raise AgentError("REQUEST_NOT_FOUND", "Команда ещё не принята либо её принятие не подтверждено.", 404, retryable=True)
        return snapshot

    @app.post("/api/v1/runs/{run_id}/stop", response_model=RunSnapshot)
    async def stop(run_id: Id):
        return await runtime.stop(run_id)

    @app.post("/api/v1/runs/{run_id}/confirm", response_model=RunSnapshot)
    async def confirm(run_id: Id, command: ConfirmCommand):
        return await runtime.confirm(run_id, command.confirmation_id,
                                     command.operation_digest, command.approved)

    @app.post("/api/v1/requests/{client_request_id}/stop", response_model=RunSnapshot | None)
    async def stop_request(client_request_id: Id):
        return await runtime.stop_request(client_request_id)

    @app.get("/api/v1/runs/{run_id}/events", response_class=EventSourceResponse)
    async def events(request: Request, run_id: Id, after: int = Query(0, ge=0, le=999999999999999999)):
        await store.get_snapshot(run_id)
        last = request.headers.get("last-event-id")
        if last is not None:
            if not last.isdecimal() or len(last) > 18:
                raise AgentError("EVENT_CURSOR_INVALID", "Неверный Last-Event-ID.", 422)
            after = int(last)
        if last is not None or after > 0:
            await store.record_metric('reconnects')
        return EventSourceResponse(run_stream(store, runtime.hub, run_id, after), ping=2)

    @app.get("/api/v1/runs/{run_id}/event-log", response_model=EventPage)
    async def event_log(run_id: Id, after: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500)):
        items = await store.get_events(run_id, after=after, limit=limit)
        snapshot = await store.get_snapshot(run_id)
        return EventPage(items=items, next_after=items[-1].seq if items and items[-1].seq < snapshot.last_seq else None)

    @app.get("/api/v1/runs/{run_id}/operations", response_model=OperationPage)
    async def operations(run_id: Id, offset: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500)):
        await store.get_snapshot(run_id)
        items = await store.get_operations(run_id, offset=offset, limit=limit)
        return OperationPage(items=items, next_offset=offset + limit if len(items) == limit else None)

    @app.get("/api/v1/operations/{operation_id}/report", response_model=ReportView)
    async def report(operation_id: Id):
        operation = await store.get_operation(operation_id)
        return {"operation_id": operation_id, "report": operation["report"], "result": operation["result"]}

    @app.get("/api/v1/metrics", response_model=DiagnosticsSummary)
    async def metrics():
        from .observability import diagnostics
        return await diagnostics(store, mcp_connected=runtime.mcp.ready, slow_subscribers=runtime.hub.slow_subscribers)

    @app.post("/api/v1/recovery/review", response_model=RecoveryReview, status_code=201)
    async def recovery_review(review: RecoveryReview):
        if review.release_for_new_context:
            from .owned_stdio import identity_alive
            async with runtime._lock:
                if not review.manual_corel_checked or not review.document_id or not review.barrier_id:
                    raise AgentError('REVIEW_CHECK_REQUIRED', 'Нужны ручная проверка CorelDRAW, точный документ и ID барьера.')
                transport = runtime.mcp.transport
                identity = getattr(transport, 'identity', None)
                if (not runtime.mcp.ready or not identity or identity_alive(identity) is not True
                        or await store.get_meta('recovery_pending', []) or await store.get_meta('recovery_only', False)
                        or runtime._tasks):
                    raise AgentError('REVIEW_OWNER_UNVERIFIED', 'Владелец MCP или завершение прежних операций не подтверждены.')
                await transport.verify_owner()
                cached = await store.get_meta('documents', {'items': [], 'stale': True})
                if cached.get('stale', True) or review.document_id not in {item['document_id'] for item in cached['items']}:
                    raise AgentError('REVIEW_DOCUMENT_STALE', 'Обновите документы и явно выберите текущий рисунок.')
                await store.release_barrier(review)
        else:
            await store.add_review(review)
        return review

    @app.get("/api/v1/recovery/reviews", response_model=RecoveryReviewPage)
    async def recovery_reviews(offset: int = Query(0, ge=0, le=999999999999999999), limit: int = Query(100, ge=1, le=500)):
        items = await store.list_reviews(offset=offset, limit=limit)
        return RecoveryReviewPage(items=items, next_offset=offset + limit if len(items) == limit else None)

    @app.post("/api/v1/recovery/reconcile", response_model=ReconcileResult)
    async def recovery_reconcile(command: EmptyCommand):
        from .recovery import reconcile
        async with runtime._lock:
            pending = await store.get_meta("recovery_pending", [])
            if runtime.mcp.ready or runtime._tasks or (not pending and not (store.directory / 'mcp-owner.json').exists()):
                raise AgentError("RECOVERY_NOT_ALLOWED", "Reconciliation разрешена только recovery-only host без активных новых операций.")
            local = os.environ.get("LOCALAPPDATA")
            if not local:
                raise AgentError("RECOVERY_ROOT_UNAVAILABLE", "Не определён фиксированный Corel runs root.", 503)
            await reconcile(store, pending, store.directory, Path(local) / "CorelDrawMcp" / "runs")
            await store.put_meta("recovery_only", True)
            return ReconcileResult()

    @app.get("/api/v1/artifacts/{artifact_id}")
    async def artifact(artifact_id: str):
        data, metadata = await assets.read_artifact(artifact_id)
        from fastapi.responses import Response
        response = Response(data, media_type=metadata["media_type"])
        response.headers["content-disposition"] = f"attachment; filename*=UTF-8''{quote(metadata['display_name'], safe='')}"
        return response

    @app.get("/api/v1/images/{image_id}")
    async def image(image_id: str):
        data, _ = await assets.read_image(image_id)
        return Response(data, media_type="image/png")

    @app.get("/api/v1/health", response_model=Health)
    async def health():
        runs = await store.list_runs(limit=500)
        active = next((run for run in runs if run.status not in TERMINAL_STATUSES), None)
        cached = await store.get_meta("documents", {})
        return Health(mcp_connected=runtime.mcp.ready, store_ready=not store.failed,
            active_run_id=active.id if active else None, active_job_id=active.job_id if active else None,
            safety_barrier=await store.get_barrier(), cached_corel_at=cached.get("at"), storage=await store.storage_state())

    return app
