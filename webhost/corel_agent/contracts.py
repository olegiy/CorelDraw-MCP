"""Versioned HTTP/runtime contracts. MCP reports retain their original structure."""

import ipaddress
from datetime import UTC, datetime
from typing import Annotated, Any, Literal
from urllib.parse import urlsplit
from uuid import uuid4

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    SecretStr,
    StrictBool,
    TypeAdapter,
    field_validator,
)

Id = Annotated[str, Field(pattern=r"^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$")]
Digest = Annotated[str, Field(pattern=r"^[a-f0-9]{64}$")]
DocumentId = Annotated[str, Field(min_length=1, max_length=100)]
RunStatus = Literal[
    "preparing", "running", "awaiting_confirmation", "stopping", "completed", "stopped",
    "failed", "uncertain", "limit_reached", "rejected", "halted", "interrupted",
]
TERMINAL_STATUSES = frozenset({
    "completed", "stopped", "failed", "uncertain", "limit_reached", "rejected", "halted",
    "interrupted",
})
JobStatus = Literal["running", "timed_out", "succeeded", "failed", "unknown"]


def new_id() -> str:
    return str(uuid4())


def utc_now() -> str:
    return datetime.now(UTC).isoformat()


class DTO(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class AgentError(Exception):
    def __init__(self, code: str, message: str, status: int = 409, *, retryable: bool = False):
        super().__init__(message)
        self.code, self.message, self.status, self.retryable = code, message, status, retryable


class ErrorResponse(DTO):
    code: str
    message: str
    trace_id: str
    retryable: bool = False


class ProfileSnapshot(DTO):
    id: Id
    name: str = Field(min_length=1, max_length=120)
    base_url: str = Field(min_length=1, max_length=1024)
    model: str = Field(min_length=1, max_length=200)
    local: StrictBool = False
    context_approved: StrictBool = False
    vision: StrictBool = False
    send_preview: StrictBool = False
    stream_responses: StrictBool = True
    credential_revision: Id = Field(default_factory=new_id)

    @field_validator("base_url")
    @classmethod
    def valid_url(cls, value):
        try:
            url = urlsplit(value)
            valid = (url.scheme in {"http", "https"} and url.hostname and not url.username
                     and not url.password and not url.query and not url.fragment
                     and not any(char.isspace() for char in value))
            _ = url.port
            if not valid:
                raise ValueError
        except ValueError:
            raise ValueError("Нужен HTTP(S)-адрес API без credentials, query и fragment.") from None
        if url.scheme == "http":
            try:
                local = ipaddress.ip_address(url.hostname).is_loopback
            except ValueError:
                local = url.hostname.lower() == "localhost"
            if not local:
                raise ValueError("HTTP разрешён только для loopback API.")
        return value.rstrip("/")


class ToolDefinition(DTO):
    name: str
    description: str
    input_schema: dict[str, Any]
    annotations: dict[str, Any] = Field(default_factory=dict)


class RunCommand(DTO):
    client_request_id: Id
    conversation_id: Id
    profile_id: Id | None = None
    document_id: DocumentId | None = None
    message: str = Field(default="", max_length=65536)
    kind: Literal["agent", "manual"] = "agent"
    tool: str | None = None
    arguments: dict[str, Any] = Field(default_factory=dict)
    read_only: StrictBool = True
    output_root_id: Id | None = None
    max_calls: int = Field(default=20, ge=1, le=100, strict=True)

    @field_validator("message")
    @classmethod
    def byte_limit(cls, value):
        if len(value.encode("utf-8")) > 65536:
            raise ValueError("Сообщение превышает 64 КиБ UTF-8.")
        return value


class PreparedOperation(DTO):
    operation_id: Id
    run_id: Id
    tool: str
    arguments: dict[str, Any]
    document_id: DocumentId | None = None
    corel_request_id: str | None = None
    policy_version: int = 1
    digest: Digest
    required_postchecks: tuple[str, ...] = ()
    file_fingerprint: str | None = None
    scope_digest: Digest | None = None


class OutputRoot(DTO):
    id: Id = Field(default_factory=new_id)
    path: str = Field(min_length=1, max_length=1024)


class FileRef(DTO):
    file_ref: Id = Field(default_factory=new_id)
    display_name: str
    path: str
    fingerprint: str


class OperationContext(DTO):
    run_id: Id
    document_id: DocumentId | None = None
    read_only: bool = True
    output_root: OutputRoot | None = None
    files: tuple[FileRef, ...] = ()
    current_document_path: str | None = None
    profile: ProfileSnapshot | None = None
    allow_new_document: bool = False


class RiskDecision(DTO):
    decision: Literal["allow", "confirm", "deny"]
    reasons: tuple[str, ...] = ()
    operation_digest: Digest


class ConfirmationRequest(DTO):
    confirmation_id: Id = Field(default_factory=new_id)
    operation_digest: Digest
    operation: PreparedOperation
    reasons: tuple[str, ...]


class ConfirmCommand(DTO):
    confirmation_id: Id
    operation_digest: Digest
    approved: StrictBool


class Counters(DTO):
    calls: int = 0
    turns: int = 0
    reserved_postchecks: int = 0


class SafetyBarrier(DTO):
    id: Id = Field(default_factory=new_id)
    reason: str
    run_id: Id | None = None
    operation_id: Id | None = None
    job_id: str | None = None
    at: str = Field(default_factory=utc_now)


class RunSnapshot(DTO):
    id: Id
    client_request_id: Id
    conversation_id: Id
    kind: Literal["agent", "manual"]
    status: RunStatus = "preparing"
    profile: ProfileSnapshot | None = None
    document_id: DocumentId | None = None
    read_only: bool = True
    output_root_id: Id | None = None
    max_calls: int = 20
    last_seq: int = 0
    stop_requested: bool = False
    pending_confirmation: ConfirmationRequest | None = None
    active_operation_id: Id | None = None
    job_id: str | None = None
    counters: Counters = Field(default_factory=Counters)
    monitoring_state: Literal["healthy", "degraded"] = "healthy"
    safety_barrier: SafetyBarrier | None = None
    created_at: str = Field(default_factory=utc_now)
    updated_at: str = Field(default_factory=utc_now)


class AcceptedRun(DTO):
    is_new: bool
    snapshot: RunSnapshot


class EventState(DTO):
    status: RunStatus
    document_id: DocumentId | None = None
    stop_requested: bool
    active_operation_id: Id | None = None
    job_id: str | None = None
    counters: Counters
    monitoring_state: Literal["healthy", "degraded"]
    safety_barrier: SafetyBarrier | None = None


class MessageDeltaData(DTO):
    text: str


class MessageData(DTO):
    id: Id
    role: Literal["user", "assistant", "tool"]
    text: str


class ToolProposedData(DTO):
    operation: PreparedOperation


class ToolStartData(DTO):
    operation_id: Id
    tool: str


class JobProgressData(DTO):
    operation_id: Id
    job_id: str
    status: JobStatus
    stage: str


class ToolResultData(DTO):
    operation_id: Id
    outcome: Literal["succeeded", "failed", "unknown"]
    applied: bool | None = None
    cleanup: dict[str, Any] | None = None
    error: dict[str, Any] | None = None


class ImageData(DTO):
    image_id: Id
    document_id: DocumentId
    page_index: int | None = None
    target: Literal["page", "selection"]
    width: int
    height: int
    at: str = Field(default_factory=utc_now)


class StatusData(DTO):
    status: RunStatus


class WarningData(DTO):
    code: str
    message: str


class EventBase(DTO):
    schema_version: Literal[1] = 1
    run_id: Id
    seq: int = Field(ge=1)
    at: str = Field(default_factory=utc_now)
    state: EventState


class MessageDeltaEvent(EventBase):
    type: Literal["message_delta"]
    data: MessageDeltaData


class MessageEvent(EventBase):
    type: Literal["message"]
    data: MessageData


class ToolProposedEvent(EventBase):
    type: Literal["tool_proposed"]
    data: ToolProposedData


class ConfirmationEvent(EventBase):
    type: Literal["confirmation"]
    data: ConfirmationRequest


class ToolStartEvent(EventBase):
    type: Literal["tool_start"]
    data: ToolStartData


class JobProgressEvent(EventBase):
    type: Literal["job_progress"]
    data: JobProgressData


class ToolResultEvent(EventBase):
    type: Literal["tool_result"]
    data: ToolResultData


class ImageEvent(EventBase):
    type: Literal["image"]
    data: ImageData


class StatusEvent(EventBase):
    type: Literal["status"]
    data: StatusData


class WarningEvent(EventBase):
    type: Literal["warning"]
    data: WarningData


class ErrorEvent(EventBase):
    type: Literal["error"]
    data: WarningData


RunEvent = Annotated[
    MessageDeltaEvent | MessageEvent | ToolProposedEvent | ConfirmationEvent | ToolStartEvent
    | JobProgressEvent | ToolResultEvent | ImageEvent | StatusEvent | WarningEvent | ErrorEvent,
    Field(discriminator="type"),
]
EVENT_ADAPTER = TypeAdapter(RunEvent)


class ImageBlock(DTO):
    mime_type: str
    data: str


class JobUpdate(DTO):
    operation_id: Id
    job_id: str
    status: JobStatus
    stage: str
    report: dict[str, Any]
    monitoring_state: Literal["healthy", "degraded"] = "healthy"
    isError: bool = False


class OperationResult(DTO):
    operation_id: Id
    job_id: str | None = None
    outcome: Literal["succeeded", "failed", "unknown"]
    report: dict[str, Any]
    applied: bool | None = None
    cleanup: dict[str, Any] | None = None
    images: tuple[ImageBlock, ...] = ()
    isError: bool = False


class ProcessIdentity(DTO):
    pid: int = Field(ge=1)
    creation_time: str


class NativeToolCall(DTO):
    id: str = Field(min_length=1, max_length=200)
    name: str = Field(min_length=1, max_length=200)
    arguments: dict[str, Any]


class Completion(DTO):
    content: str | None = None
    tool_calls: tuple[NativeToolCall, ...] = ()


class DocumentInfo(DTO):
    document_id: DocumentId
    name: str
    path: str = ""
    dirty: bool = False
    pages: int = 1
    active_page: int = 1
    unit: int | None = None
    preserve_selection: bool | None = None


class DocumentsCache(DTO):
    items: list[DocumentInfo] = Field(default_factory=list)
    at: str | None = None
    stale: bool = True
    active_document_id: DocumentId | None = None


class RunPage(DTO):
    items: list[RunSnapshot]
    next_offset: int | None = None


class EventPage(DTO):
    items: list[RunEvent]
    next_after: int | None = None


class OperationView(DTO):
    id: Id
    run_id: Id
    prepared: PreparedOperation
    state: Literal["prepared", "dispatching", "known_job", "terminal"]
    job_id: str | None = None
    report: dict[str, Any] | None = None
    result: OperationResult | None = None


class OperationPage(DTO):
    items: list[OperationView]
    next_offset: int | None = None


class Health(DTO):
    schema_version: Literal[1] = 1
    mcp_connected: bool
    store_ready: bool
    active_run_id: Id | None = None
    active_job_id: str | None = None
    safety_barrier: SafetyBarrier | None = None
    cached_corel_at: str | None = None


class SessionResponse(DTO):
    authenticated: Literal[True] = True
    csrf_token: str = Field(pattern=r"^[A-Za-z0-9_-]{43}$", repr=False)


class ConversationCreate(DTO):
    title: str = Field(default="Новая переписка", min_length=1, max_length=120)


class Conversation(DTO):
    id: Id
    title: str
    created_at: str


class ConversationPage(DTO):
    items: list[Conversation]
    next_offset: int | None = None


class MessageView(DTO):
    id: Id
    run_id: Id | None = None
    role: Literal["user", "assistant", "tool"]
    text: str
    at: str


class MessagePage(DTO):
    items: list[MessageView]
    next_offset: int | None = None


class Workspace(DTO):
    output_roots: list[OutputRoot] = Field(default_factory=list, max_length=8)


class FileRegister(DTO):
    path: str = Field(min_length=1, max_length=1024)


class PublicFileRef(DTO):
    file_ref: Id
    display_name: str


class ProfileUpdate(DTO):
    profile: ProfileSnapshot
    api_key: SecretStr | None = Field(default=None, repr=False)


class ProfilePage(DTO):
    items: list[ProfileSnapshot]


class ReportView(DTO):
    operation_id: Id
    report: dict[str, Any] | None = None
    result: OperationResult | None = None


class ProfileTestResult(DTO):
    native_tools: Literal[True] = True
    message: str = "Модель вернула валидированный native tool call; CorelDRAW не вызывался."


class Artifact(DTO):
    artifact_id: Id
    display_name: str
    media_type: Literal["image/png", "image/svg+xml", "application/pdf", "application/json", "application/octet-stream"]
    bytes: int


class StorageState(DTO):
    bytes: int
    free_bytes: int
    quota_bytes: int
    warning: bool
    full: bool


class MetricSummary(DTO):
    runs: int
    llm_turns: int
    corel_operations: int
    approvals: int
    outcomes: dict[str, int]
    slow_subscribers: int


class DiagnosticsSummary(DTO):
    schema_version: Literal[1] = 1
    at: str
    mcp_connected: bool
    store_ready: bool
    storage: StorageState
    metrics: MetricSummary
    safety_barrier_present: bool


class RecoveryReview(DTO):
    note: str = Field(min_length=1, max_length=4000)
    document_id: DocumentId | None = None
    at: str = Field(default_factory=utc_now)


class RecoveryReviewPage(DTO):
    items: list[RecoveryReview]
