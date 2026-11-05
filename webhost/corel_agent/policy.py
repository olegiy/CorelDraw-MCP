"""Explicit Corel capability table intersected with the registered MCP catalog."""

import asyncio
import hashlib
import ntpath
import os
import re
import stat
from copy import deepcopy
from pathlib import Path, PureWindowsPath

from jsonschema import Draft202012Validator

from .assets import assert_no_links
from .contracts import AgentError, FileRef, PreparedOperation, RiskDecision, ToolDefinition, new_id
from .privacy import Privacy
from .store import canonical, fingerprint

POLICY_VERSION = 1
READ_ONLY = frozenset({"corel_status", "corel_inspect_document", "corel_preview"})
NEW_DOCUMENT = frozenset({"corel_create_document", "corel_open_document"})
MVP_TOOLS = READ_ONLY | NEW_DOCUMENT | frozenset({
    "corel_execute_vba", "corel_save_document", "corel_export", "corel_close_document",
})
SYSTEM_FIELDS = frozenset({
    "request_id", "document_id", "deadline_seconds", "read_only", "base_url", "profile_id",
    "credential_revision", "output_root_id", "policy_version", "operation_id", "run_id",
})
EXPORT_ONLY = frozenset({"color_mode", "jpeg_compression", "tiff_compression", "use_document_profile", "transparent", "text_mode"})
POSTCHECKS = {
    "corel_execute_vba": ("corel_inspect_document", "corel_preview"),
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
    @staticmethod
    def filename(name):
        if (not name or len(name) > 240 or name in {".", ".."} or name.endswith((".", " "))
                or any(ord(char) < 32 or char in '<>:"/\\|?*' for char in name)
                or name.split(".", 1)[0].upper() in RESERVED):
            raise AgentError("PATH_DENIED", "Недопустимое локальное имя файла.", 403)
        return name

    def path(self, value):
        if (not isinstance(value, str) or not value or len(value) > 1024
                or not re.match(r"^[A-Za-z]:[/\\]", value) or not PureWindowsPath(value).is_absolute()
                or any(ord(char) < 32 for char in value)):
            raise AgentError("PATH_DENIED", "Разрешены только явные локальные пути Windows, без UNC/device/NUL.", 403)
        parts = re.split(r"[/\\]", value)[1:]
        for part in parts:
            if part:
                self.filename(part)
        path = Path(value)
        assert_no_links(path)
        path = path.resolve()
        assert_no_links(path)
        return path

    def directory(self, value):
        path = self.path(value)
        if not path.is_dir():
            raise AgentError("ROOT_UNAVAILABLE", "Операторский каталог должен уже существовать.", 403)
        return path

    def file_fingerprint(self, value):
        path = self.path(value)
        if not path.exists():
            if not path.parent.is_dir():
                raise AgentError("ROOT_UNAVAILABLE", "Родительский каталог недоступен.", 403)
            return None
        with path.open("rb") as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
                raise AgentError("PATH_DENIED", "Нужен обычный файл без hard links.", 403)
            digest = hashlib.sha256()
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
            after = os.fstat(stream.fileno())
        assert_no_links(path)
        current = path.stat()
        def identity(info):
            return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)
        if identity(before) != identity(after) or identity(after) != identity(current):
            raise AgentError("FILE_CHANGED", "Файл изменился при проверке.")
        return fingerprint({"identity": identity(after), "sha256": digest.hexdigest()})

    async def register_file(self, value):
        def register():
            path = self.path(value)
            if path.suffix.lower() != ".cdr" or not path.is_file():
                raise AgentError("FILE_DENIED", "Можно зарегистрировать только существующий обычный CDR.", 403)
            return FileRef(display_name=path.name, path=str(path), fingerprint=self.file_fingerprint(str(path)))
        try:
            return await asyncio.to_thread(register)
        except OSError:
            raise AgentError("FILE_UNAVAILABLE", "Локальный CDR недоступен.", 403) from None

    def output(self, token, root, extension):
        if not root:
            raise AgentError("OUTPUT_ROOT_REQUIRED", "Сначала выберите операторский output root.", 403)
        if not isinstance(token, str) or not token.startswith("@output/"):
            raise AgentError("PATH_DENIED", "Назначение задаётся только как @output/имя.", 403)
        name = self.filename(token[len("@output/"):])
        if Path(name).suffix.lower() != f".{extension}":
            raise AgentError("EXTENSION_INVALID", "Расширение должно соответствовать выбранному формату.", 422)
        root_path = self.directory(root.path)
        path = self.path(str(root_path / name))
        if path.parent != root_path:
            raise AgentError("PATH_DENIED", "Назначение вне выбранного output root.", 403)
        return str(path)


class Policy:
    def __init__(self, catalog, *, privacy=None):
        self.registry = {tool.name: tool for tool in catalog if tool.name in MVP_TOOLS}
        self.paths, self.privacy = PathGuard(), privacy or Privacy()

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
        properties = schema.get("properties", {})
        for key in SYSTEM_FIELDS:
            properties.pop(key, None)
        if name == "corel_preview":
            properties.pop("range", None)
            properties["target"] = {"type": "string", "enum": ["page", "selection"], "default": "page"}
        if name == "corel_export":
            properties["format"] = {"type": "string", "enum": ["png", "svg", "pdf"]}
            for key in EXPORT_ONLY:
                properties.pop(key, None)
        if name in {"corel_save_document", "corel_export"}:
            properties["path"] = {"type": "string", "pattern": r"^@output/[^/\\]{1,240}$",
                                  "description": "Имя файла внутри выбранного оператором output root."}
        if name == "corel_open_document":
            properties["path"] = {"type": "string", "enum": [item.file_ref for item in context.files],
                                  "description": "Только file_ref ранее выбранного оператором CDR, не абсолютный путь."}
        if "static_ids" in properties:
            properties["static_ids"]["uniqueItems"] = True
        schema["required"] = [key for key in schema.get("required", []) if key not in SYSTEM_FIELDS]
        schema["additionalProperties"] = False
        return schema

    def catalog(self, context):
        result = []
        for name, tool in self.registry.items():
            if name == "corel_close_document" or (context.read_only and name not in READ_ONLY):
                continue
            if name in NEW_DOCUMENT:
                if context.document_id or not context.allow_new_document:
                    continue
                if name == "corel_open_document" and not context.files:
                    continue
            elif name != "corel_status" and not context.document_id:
                continue
            if name == "corel_export" and context.output_root is None:
                continue
            description = tool.description if name != "corel_export" else "Экспорт MVP: PNG/SVG/PDF в выбранный output root; перезапись требует approval."
            result.append(ToolDefinition(name=name, description=description,
                input_schema=self.input_schema(name, context), annotations=tool.annotations))
        return result

    async def prepare(self, tool, arguments, context):
        def prepare():
            if not isinstance(arguments, dict):
                raise AgentError("ARGUMENTS_INVALID", "Нужен объект аргументов.", 422)
            if SYSTEM_FIELDS & arguments.keys():
                raise AgentError("BINDING_OVERRIDE", "Привязку, request_id, deadline и permissions задаёт только host.", 403)
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
                if context.document_id or not context.allow_new_document:
                    raise AgentError("DOCUMENT_INTENT_REQUIRED", "Новый документ должен быть явно запрошен без другой привязки.")
            elif tool != "corel_status" and not context.document_id:
                raise AgentError("DOCUMENT_REQUIRED", "Выберите точный документ для этого Run.")
            args = deepcopy(arguments)
            for key, property_schema in schema.get("properties", {}).items():
                if key not in args and "default" in property_schema:
                    args[key] = deepcopy(property_schema["default"])
            file_digest = None
            if tool == "corel_open_document":
                reference = next((item for item in context.files if item.file_ref == args["path"]), None)
                if reference is None:
                    raise AgentError("FILE_REF_DENIED", "Нет операторской ссылки на CDR.", 403)
                args["path"] = str(self.paths.path(reference.path))
                file_digest = self.paths.file_fingerprint(args["path"])
                if file_digest != reference.fingerprint:
                    raise AgentError("FILE_CHANGED", "Зарегистрированный CDR изменился; выберите его снова.")
            if tool in {"corel_save_document", "corel_export"}:
                if tool == "corel_save_document" and "path" not in args:
                    if not context.current_document_path:
                        raise AgentError("SAVE_PATH_REQUIRED", "Несохранённому CDR нужен явный @output/имя.cdr.")
                    args["path"] = str(self.paths.path(context.current_document_path))
                    if Path(args["path"]).suffix.lower() != ".cdr":
                        raise AgentError("PATH_DENIED", "Текущий файл должен быть локальным CDR.", 403)
                else:
                    extension = "cdr" if tool == "corel_save_document" else args["format"]
                    args["path"] = self.paths.output(args["path"], context.output_root, extension)
                file_digest = self.paths.file_fingerprint(args["path"])
                selected_save = (tool == "corel_save_document" and context.current_document_path
                                 and ntpath.normcase(args["path"]) == ntpath.normcase(context.current_document_path))
                if file_digest is not None and not selected_save and not args.get("overwrite", False):
                    raise AgentError("FILE_EXISTS", "Файл существует; требуется явный overwrite и approval.")
                if tool == "corel_export":
                    if ((args.get("range", "page") == "document" and args["format"] != "pdf")
                            or (args.get("range") == "selection" and args["format"] == "pdf")):
                        raise AgentError("EXPORT_RANGE_INVALID", "PDF поддерживает страницу/документ, PNG/SVG страницу/выделение.", 422)
            if tool == "corel_execute_vba":
                self.privacy.validate_vba(args["code"])
            operation_id = new_id()
            request_id = f"web:{operation_id}" if tool not in READ_ONLY else None
            if request_id:
                args["request_id"] = request_id
            args["deadline_seconds"] = 600
            if tool not in NEW_DOCUMENT and tool != "corel_status":
                args["document_id"] = context.document_id
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
            if (operation.tool not in self.registry or operation.policy_version != POLICY_VERSION
                    or operation.digest != self.operation_digest(operation)):
                return decision("deny", "OPERATION_CHANGED: параметры изменились или политика несовместима.")
            if operation.scope_digest != self.scope_digest(current_state):
                return decision("deny", "SCOPE_CHANGED: документ или разрешения изменились.")
            if current_state.read_only and operation.tool not in READ_ONLY:
                return decision("deny", "READ_ONLY: изменение запрещено режимом Run.")
            if "path" in operation.arguments:
                actual = self.paths.file_fingerprint(operation.arguments["path"])
                if actual != operation.file_fingerprint:
                    return decision("deny", "FILE_CHANGED: файл изменился после подготовки операции.")
            if operation.tool == "corel_execute_vba":
                self.privacy.validate_vba(operation.arguments["code"])
                return decision("confirm", "VBA имеет полный доступ к CorelDRAW и ОС; document_id не является sandbox.")
            if operation.tool == "corel_close_document":
                return decision("confirm", "Закрытие точного документа может привести к потере несохранённых изменений.")
            if operation.tool in {"corel_save_document", "corel_export"} and operation.file_fingerprint:
                if operation.arguments.get("overwrite") is True:
                    return decision("confirm", "Явная перезапись существующего файла требует согласия; кэш текущего пути CDR не подтверждает его актуальную роль.")
                selected_save = (operation.tool == "corel_save_document" and current_state.current_document_path
                    and ntpath.normcase(operation.arguments["path"]) == ntpath.normcase(current_state.current_document_path))
                if not selected_save:
                    return decision("confirm", "Перезапись конкретного существующего файла. Проверка fingerprint не является атомарной блокировкой нативного Save/Finish.")
            return decision("allow")
        try:
            return await asyncio.to_thread(classify)
        except (AgentError, OSError, ValueError, TypeError):
            return RiskDecision(decision="deny", reasons=("VALIDATION_FAILED: операция больше не безопасна для отправки.",), operation_digest=operation.digest)
