"""Single frozen OpenAI-compatible endpoint, native tools only, no retry/fallback."""

import asyncio
import time

import httpx
from jsonschema import Draft202012Validator
from pydantic import SecretStr, ValidationError

from .contracts import AgentError, Completion, NativeToolCall
from .privacy import Privacy
from .streaming import CompletionStream, strict_json

MAX_RESPONSE = 8 * 1024**2


class Provider:
    def __init__(self, profile, secret: SecretStr, *, privacy=None, transport=None):
        self.profile = profile
        self.privacy = privacy or Privacy()
        self.privacy.register_secret(secret.get_secret_value())
        self._client = httpx.AsyncClient(base_url=profile.base_url + "/", trust_env=False,
            follow_redirects=False, timeout=httpx.Timeout(120.0, connect=10.0), transport=transport,
            headers={"Authorization": f"Bearer {secret.get_secret_value()}"} if secret.get_secret_value() else {})
        self._slot = asyncio.Semaphore(1)
        self.last_metrics = None

    async def aclose(self):
        await self._client.aclose()

    def _completion(self, data, tools):
        try:
            if not isinstance(data, dict) or "error" in data or len(data["choices"]) != 1:
                raise ValueError
            choice = data["choices"][0]
            if choice["finish_reason"] not in {"stop", "tool_calls"}:
                raise ValueError
            message = choice["message"]
            if message.get("role", "assistant") != "assistant":
                raise ValueError
            content = message.get("content")
            if content is not None and not isinstance(content, str):
                raise ValueError
            calls = message.get("tool_calls") or []
            if not isinstance(calls, list) or len(calls) > 100 or (content is None and not calls):
                raise ValueError
            if bool(calls) != (choice["finish_reason"] == "tool_calls"):
                raise ValueError
            registry = {tool.name: tool for tool in tools}
            ids, result = set(), []
            for call in calls:
                if call["type"] != "function" or call["id"] in ids:
                    raise ValueError
                ids.add(call["id"])
                function = call["function"]
                if function["name"] not in registry or not isinstance(function["arguments"], str):
                    raise ValueError
                arguments = strict_json(function["arguments"])
                if not isinstance(arguments, dict) or list(Draft202012Validator(
                    registry[function["name"]].input_schema).iter_errors(arguments)):
                    raise ValueError
                if self.privacy.contains_secret(arguments):
                    raise ValueError
                result.append(NativeToolCall(id=call["id"], name=function["name"], arguments=arguments))
            return Completion(content=self.privacy.sanitize_local(content), tool_calls=tuple(result))
        except (ValueError, TypeError, KeyError, IndexError, AttributeError, ValidationError):
            raise AgentError("MODEL_RESPONSE_INVALID", "Ответ модели прерван или содержит некорректные native tool calls; выполнение запрещено.", 502) from None

    async def complete(self, history, tools, emit_delta=None):
        for message in history:
            content = message.get('content')
            if isinstance(content, list) and any(item.get('type') == 'image_url' for item in content):
                if not self.profile.vision or not self.profile.send_preview:
                    raise AgentError('PREVIEW_CONSENT_REQUIRED', 'Передача превью требует обоих frozen consent flags.', 403)
                for item in content:
                    if item.get('type') == 'image_url':
                        url = item.get('image_url', {}).get('url', '')
                        if not isinstance(url, str) or not url.startswith('data:image/png;base64,'):
                            raise AgentError('PREVIEW_INVALID', 'Разрешено только проверенное inline PNG без внешнего URL.', 422)
        started = time.perf_counter()
        async with self._slot:
            payload = {"model": self.profile.model, "messages": self.privacy.for_model(history),
                       "stream": self.profile.stream_responses}
            if tools:
                payload["tools"] = [{"type": "function", "function": {"name": tool.name,
                    "description": tool.description, "parameters": tool.input_schema}} for tool in tools]
                payload["tool_choice"] = "auto"
            try:
                async with asyncio.timeout(120):
                    async with self._client.stream("POST", "chat/completions", json=payload) as response:
                        if not response.is_success:
                            raise AgentError("PROVIDER_HTTP_ERROR", f"API модели вернул HTTP {response.status_code}; повтор не выполнялся.", 502)
                        body = bytearray()
                        async for chunk in response.aiter_bytes():
                            if len(body) + len(chunk) > MAX_RESPONSE:
                                raise ValueError("Response limit")
                            body.extend(chunk)
                        if not self.profile.stream_responses:
                            data = strict_json(bytes(body))
                        else:
                            stream = CompletionStream()
                            pending = []
                            for line in bytes(body).decode("utf-8").splitlines():
                                if line.startswith("data:"):
                                    pending.append(line[5:].lstrip(" "))
                                elif not line and pending:
                                    stream.feed("\n".join(pending))
                                    pending.clear()
                            if pending:
                                stream.feed("\n".join(pending))
                            data = stream.result()
                        completion = self._completion(data, tools)
                        # Emit only validated, sanitized text: a credential split across chunks cannot leak.
                        if emit_delta and completion.content:
                            await emit_delta(completion.content)
                        return completion
            except AgentError:
                raise
            except (httpx.HTTPError, TimeoutError):
                raise AgentError("PROVIDER_UNAVAILABLE", "API модели недоступен или истёк срок ожидания; retry/fallback не выполнялся.", 502) from None
            except (ValueError, TypeError, KeyError, AttributeError, UnicodeError):
                raise AgentError("MODEL_RESPONSE_INVALID", "API вернул неполный или некорректный ограниченный ответ.", 502) from None
            finally:
                self.last_metrics = {"duration_seconds": time.perf_counter() - started}
