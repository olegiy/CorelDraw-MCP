"""Explicit Corel capability table intersected with the registered MCP catalog."""

import asyncio
import hashlib
import ntpath
import os
import re
import stat
from copy import deepcopy
from pathlib import Path

from jsonschema import Draft202012Validator

from .contracts import AgentError, PreparedOperation, RiskDecision, ToolDefinition, new_id
from .privacy import Privacy
from .store import canonical, fingerprint

POLICY_VERSION = 3
GLOBAL_TOOLS = frozenset({'corel_status', 'corel_describe_api'})
READ_ONLY = GLOBAL_TOOLS | frozenset({"corel_inspect_document", "corel_preview",
    'corel_measure_shapes', 'corel_inspect_curve', 'corel_find_text', 'corel_preflight'})
NEW_DOCUMENT = frozenset({"corel_create_document", "corel_open_document"})
DRAWING_MUTATIONS = frozenset({
    'corel_create_batch', 'corel_update_batch',
    'corel_create_shapes', 'corel_transform_shapes', 'corel_duplicate_shapes', 'corel_style_shapes',
    'corel_manage_layers', 'corel_group_shapes', 'corel_boolean_shapes', 'corel_create_text',
    'corel_convert_to_curves', 'corel_create_curve', 'corel_edit_curve', 'corel_edit_text',
    'corel_align_shapes', 'corel_grid_duplicate', 'corel_layout_shapes', 'corel_import',
    'corel_manage_pages', 'corel_powerclip',
})
MVP_TOOLS = READ_ONLY | NEW_DOCUMENT | frozenset({
    "corel_save_document", "corel_export", "corel_close_document",
}) | DRAWING_MUTATIONS
SYSTEM_FIELDS = frozenset({
    "request_id", "document_id", "deadline_seconds", "base_url", "profile_id", 'new_document', 'output_directory',
    "credential_revision", "policy_version", "operation_id", "run_id", 'sha256', 'size_bytes',
})
EXPORT_ONLY = frozenset({"color_mode", "jpeg_compression", "tiff_compression", "use_document_profile", "transparent", "text_mode"})
POSTCHECKS = {
    **{tool: ("corel_inspect_document", "corel_preview") for tool in DRAWING_MUTATIONS},
    "corel_create_document": ("corel_inspect_document",),
    "corel_open_document": ("corel_inspect_document",),
    "corel_save_document": ("corel_inspect_document",),
    "corel_export": ("corel_inspect_document",),
    "corel_close_document": ("corel_status",),
}
RESERVED = frozenset({"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"}) | frozenset(
    f"{prefix}{number}" for prefix in ("COM", "LPT") for number in "123456789¹²³"
)


class PathGuard:
    def __init__(self, protected_paths=()):
        self.protected_paths = tuple(Path(path).resolve() for path in protected_paths)

    @staticmethod
    def filename(name):
        if (not name or len(name) > 240 or name in {".", ".."} or name.endswith((".", " "))
                or any(ord(char) < 32 or char in '<>:"/\\|?*' for char in name)
                or name.split(".", 1)[0].upper() in RESERVED):
            raise AgentError("PATH_DENIED", "Недопустимое локальное имя файла.", 403)
        return name

    def path(self, value):
        if (not isinstance(value, str) or not value or len(value) > 1024
                or not ntpath.isabs(value)
                or any(ord(char) < 32 for char in value)):
            raise AgentError("PATH_DENIED", "Нужен абсолютный путь Windows без NUL и управляющих символов.", 403)
        parts = re.split(r"[/\\]", ntpath.splitdrive(value)[1])
        for part in parts:
            if part and part not in {'.', '..'}:
                self.filename(part)
        path = Path(value).resolve()
        if any(path == protected or protected in path.parents for protected in self.protected_paths):
            raise AgentError('PATH_PROTECTED', 'Служебные history/assets/reports/marker пути не являются пользовательским назначением.', 403)
        return path

    def directory(self, value):
        path = self.path(value)
        if not path.is_dir():
            raise AgentError("ROOT_UNAVAILABLE", "Каталог назначения должен уже существовать.", 403)
        return path

    def input_fingerprint(self, value):
        path = self.path(value)
        if not path.exists():
            if not path.parent.is_dir():
                raise AgentError("ROOT_UNAVAILABLE", "Родительский каталог недоступен.", 403)
            return None
        with path.open("rb") as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode) or before.st_size > 256 * 1024**2:
                raise AgentError("FILE_UNAVAILABLE", "Нужен обычный файл не более 256 МиБ.", 403)
            digest = hashlib.sha256()
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
            after = os.fstat(stream.fileno())
        current = path.stat()
        def identity(info):
            return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)
        if identity(before) != identity(after) or identity(after) != identity(current):
            raise AgentError("FILE_CHANGED", "Файл изменился при проверке.")
        sha256 = digest.hexdigest()
        return fingerprint({"identity": identity(after), "sha256": sha256}), sha256, after.st_size

    def file_fingerprint(self, value):
        result = self.input_fingerprint(value)
        return result[0] if result else None

    def output(self, value, extension):
        path = self.path(value)
        if path.suffix.lower() != f".{extension}":
            raise AgentError("EXTENSION_INVALID", "Расширение должно соответствовать выбранному формату.", 422)
        if not path.parent.is_dir():
            raise AgentError('ROOT_UNAVAILABLE', 'Родительский каталог назначения недоступен.', 403)
        if path.exists() and not path.is_file():
            raise AgentError('FILE_UNAVAILABLE', 'Назначение должно быть обычным файлом.', 403)
        return str(path)

    def manual_arguments(self, tool, arguments, output_directory):
        args = deepcopy(arguments)
        value = args.get('path')
        if tool in {'corel_save_document', 'corel_export'} and isinstance(value, str) and not ntpath.isabs(value):
            name = self.filename(value)
            if not output_directory:
                raise AgentError('SAVE_PATH_REQUIRED', 'Относительному имени нужен каталог результатов либо абсолютный путь.', 422)
            args['path'] = str(self.directory(output_directory) / name)
        return args


class Policy:
    def __init__(self, catalog, *, privacy=None, protected_paths=()):
        self.registry = {tool.name: tool for tool in catalog if tool.name in MVP_TOOLS}
        self.paths, self.privacy = PathGuard(protected_paths), privacy or Privacy()

    @staticmethod
    def scope_digest(context):
        return fingerprint(context)

    @staticmethod
    def operation_digest(operation):
        return fingerprint(operation.model_dump(mode="json", exclude={"digest"}))

    def input_schema(self, name, context):
        if name not in self.registry:
            raise AgentError("TOOL_DENIED", "Инструмент не входит в зарегистрированный MVP-каталог.", 403)
        schema = deepcopy(self.registry[name].input_schema)
        def strip_host_fields(branch):
            properties = branch.get('properties', {})
            for key in SYSTEM_FIELDS:
                properties.pop(key, None)
            branch['required'] = [key for key in branch.get('required', []) if key not in SYSTEM_FIELDS]
            if 'static_ids' in properties:
                properties['static_ids']['uniqueItems'] = True
            for keyword in ('oneOf', 'anyOf', 'allOf'):
                for alternative in branch.get(keyword, []):
                    strip_host_fields(alternative)
        strip_host_fields(schema)
        properties = schema.get("properties", {})
        if name == "corel_preview":
            properties.pop("range", None)
            properties["target"] = {"type": "string", "enum": ["page", "selection"], "default": "page"}
        if name == "corel_export":
            properties["format"] = {"type": "string", "enum": ["png", "svg", "pdf"]}
            for key in EXPORT_ONLY:
                properties.pop(key, None)
        if 'properties' in schema:
            schema["additionalProperties"] = False
        return schema

    def catalog(self, context):
        result = []
        for name, tool in self.registry.items():
            if name in NEW_DOCUMENT:
                if context.document_id or not context.new_document:
                    continue
            elif name not in GLOBAL_TOOLS and not context.document_id:
                continue
            description = tool.description if name != "corel_export" else "Экспорт PNG/SVG/PDF по абсолютному пути; существующий файл заменяется автоматически."
            result.append(ToolDefinition(name=name, description=description,
                input_schema=self.input_schema(name, context), annotations=tool.annotations))
        return result

    async def prepare(self, tool, arguments, context):
        def prepare():
            if not isinstance(arguments, dict):
                raise AgentError("ARGUMENTS_INVALID", "Нужен объект аргументов.", 422)
            if SYSTEM_FIELDS & arguments.keys():
                raise AgentError("BINDING_OVERRIDE", "Привязку, request_id и deadline задаёт только host.", 403)
            if self.privacy.contains_secret(arguments):
                raise AgentError("SECRET_IN_OPERATION", "Credential value не отправляется в Corel MCP.", 403)
            try:
                canonical(arguments)
            except (ValueError, TypeError):
                raise AgentError("ARGUMENTS_INVALID", "Нужны конечные JSON-значения.", 422) from None
            schema = self.input_schema(tool, context)
            if list(Draft202012Validator(schema).iter_errors(arguments)):
                raise AgentError("ARGUMENTS_INVALID", "Аргументы не соответствуют MVP-схеме.", 422)
            if tool in NEW_DOCUMENT:
                if context.document_id or not context.new_document:
                    raise AgentError("DOCUMENT_INTENT_REQUIRED", "Новый документ должен быть явно запрошен без другой привязки.")
            elif tool not in GLOBAL_TOOLS and not context.document_id:
                raise AgentError("DOCUMENT_REQUIRED", "Выберите точный документ для этого Run.")
            args = deepcopy(arguments)
            for key, property_schema in schema.get("properties", {}).items():
                if key not in args and "default" in property_schema:
                    args[key] = deepcopy(property_schema["default"])
            file_digest = None
            if tool == "corel_open_document":
                path = self.paths.path(args['path'])
                if path.suffix.lower() != '.cdr' or not path.is_file():
                    raise AgentError('FILE_UNAVAILABLE', 'Открытие требует существующего обычного CDR.', 403)
                args["path"] = str(path)
                file_digest = self.paths.file_fingerprint(args["path"])
            if tool == 'corel_import':
                from .assets import assert_no_links
                value = args['path']
                if not re.match(r'^[A-Za-z]:[\\/]', value):
                    raise AgentError('INPUT_NOT_LOCAL', 'Импорт требует путь на локальном диске Windows.', 403)
                assert_no_links(Path(value))
                path = self.paths.path(value)
                extensions = {'svg': {'.svg'}, 'pdf': {'.pdf'}, 'png': {'.png'}, 'jpeg': {'.jpg', '.jpeg'}}
                if path.suffix.lower() not in extensions[args['format']] or not path.is_file():
                    raise AgentError('FILE_UNAVAILABLE', 'Файл импорта должен существовать и соответствовать формату.', 403)
                args['path'] = str(path)
                file_digest, args['sha256'], args['size_bytes'] = self.paths.input_fingerprint(args['path'])
            if tool in {"corel_save_document", "corel_export"}:
                if tool == "corel_save_document" and "path" not in args:
                    if not context.current_document_path:
                        raise AgentError("SAVE_PATH_REQUIRED", "Несохранённому CDR нужен абсолютный путь назначения.")
                    args["path"] = context.current_document_path
                extension = "cdr" if tool == "corel_save_document" else args["format"]
                args["path"] = self.paths.output(args["path"], extension)
                if tool == "corel_export":
                    if ((args.get("range", "page") == "document" and args["format"] != "pdf")
                            or (args.get("range") == "selection" and args["format"] == "pdf")):
                        raise AgentError("EXPORT_RANGE_INVALID", "PDF поддерживает страницу/документ, PNG/SVG страницу/выделение.", 422)
            operation_id = new_id()
            request_id = f"web:{operation_id}" if tool not in READ_ONLY else None
            if request_id:
                args["request_id"] = request_id
            args["deadline_seconds"] = 600
            if tool not in NEW_DOCUMENT and tool not in GLOBAL_TOOLS:
                args["document_id"] = context.document_id
            if self.privacy.contains_secret(args):
                raise AgentError('SECRET_IN_OPERATION', 'Credential value не отправляется в Corel MCP.', 403)
            if list(Draft202012Validator(self.registry[tool].input_schema).iter_errors(args)):
                raise AgentError("ARGUMENTS_INVALID", "Подготовленные аргументы нарушают MCP-схему.", 422)
            operation = PreparedOperation(operation_id=operation_id, run_id=context.run_id, tool=tool,
                arguments=args, document_id=context.document_id, corel_request_id=request_id,
                policy_version=POLICY_VERSION, digest="0" * 64, required_postchecks=POSTCHECKS.get(tool, ()),
                file_fingerprint=file_digest, scope_digest=self.scope_digest(context))
            return operation.model_copy(update={"digest": self.operation_digest(operation)})
        try:
            return await asyncio.to_thread(prepare)
        except OSError:
            raise AgentError("PATH_UNAVAILABLE", "Локальный путь недоступен; отправка запрещена.", 403) from None

    async def classify(self, operation, current_state):
        def classify():
            def decision(kind, *reasons):
                return RiskDecision(decision=kind, reasons=reasons, operation_digest=operation.digest)
            if self.privacy.contains_secret(operation.arguments):
                return decision('deny', 'SECRET_IN_OPERATION: credential value не отправляется в Corel MCP.')
            if (operation.tool not in self.registry or operation.policy_version != POLICY_VERSION
                    or operation.digest != self.operation_digest(operation)):
                return decision("deny", "OPERATION_CHANGED: параметры изменились или политика несовместима.")
            if operation.scope_digest != self.scope_digest(current_state):
                return decision("deny", "SCOPE_CHANGED: зафиксированный контекст изменился.")
            if operation.tool in {'corel_open_document', 'corel_import'}:
                if operation.tool == 'corel_import':
                    from .assets import assert_no_links
                    assert_no_links(Path(operation.arguments['path']))
                actual = self.paths.file_fingerprint(operation.arguments["path"])
                if actual != operation.file_fingerprint:
                    return decision("deny", "FILE_CHANGED: файл изменился после подготовки операции.")
            return decision("allow")
        try:
            return await asyncio.to_thread(classify)
        except (AgentError, OSError, ValueError, TypeError):
            return RiskDecision(decision="deny", reasons=("VALIDATION_FAILED: операция больше не безопасна для отправки.",), operation_digest=operation.digest)
