import json

import httpx
import pytest
from conftest import implementation
from pydantic import SecretStr
from test_settings import profile

from corel_agent.contracts import AgentError, ToolDefinition

TOOL = ToolDefinition(name="corel_preview", description="Превью", input_schema={
    "type": "object", "properties": {"target": {"type": "string", "enum": ["page", "selection"]}},
    "additionalProperties": False,
})


def response(calls=None, content="Ответ"):
    return {"choices": [{"finish_reason": "tool_calls" if calls else "stop", "message": {
        "role": "assistant", "content": content, **({"tool_calls": calls} if calls else {})}}]}


def call(name="corel_preview", arguments='{"target":"selection"}', id="call-1"):
    return {"id": id, "type": "function", "function": {"name": name, "arguments": arguments}}


async def test_native_call_uses_frozen_model_and_validated_arguments():
    requests = []
    async def handler(request):
        requests.append(request)
        return httpx.Response(200, json=response([call()]))
    provider = implementation("provider").Provider(profile(stream_responses=False), SecretStr("fixture-key"),
                                                    transport=httpx.MockTransport(handler))
    try:
        result = await provider.complete([{"role": "user", "content": "Превью"}], [TOOL])
        assert result.tool_calls[0].arguments == {"target": "selection"}
        assert result.tool_calls[0].id == "call-1"
        payload = json.loads(requests[0].content)
        assert payload["model"] == "fixture" and payload["tools"][0]["function"]["name"] == "corel_preview"
        assert requests[0].headers["authorization"] == "Bearer fixture-key"
    finally:
        await provider.aclose()


async def test_provider_refuses_image_content_without_frozen_consent():
    requests = []
    def handler(request):
        requests.append(request)
        return httpx.Response(200, json=response())
    provider = implementation('provider').Provider(profile(stream_responses=False), SecretStr('fake'),
                                                    transport=httpx.MockTransport(handler))
    try:
        with pytest.raises(AgentError) as caught:
            await provider.complete([{'role': 'user', 'content': [{'type': 'image_url',
                'image_url': {'url': 'data:image/png;base64,AAAA'}}]}], [])
        assert caught.value.code == 'PREVIEW_CONSENT_REQUIRED'
        assert requests == []
    finally:
        await provider.aclose()


@pytest.mark.parametrize("calls", [
    [call(name="unknown")], [call(arguments="not json")], [call(arguments='{"target":"bad"}')],
    [call(), call()], [call(arguments='{"target":"page","target":"selection"}')],
    [call(arguments='{"target":NaN}')], [call(arguments='{"document_id":"other"}')],
])
async def test_invalid_native_calls_never_become_completion(calls):
    provider = implementation("provider").Provider(profile(stream_responses=False), SecretStr("fixture-key"),
        transport=httpx.MockTransport(lambda request: httpx.Response(200, json=response(calls))))
    try:
        with pytest.raises(AgentError) as caught:
            await provider.complete([{"role": "user", "content": "test"}], [TOOL])
        assert caught.value.code == "MODEL_RESPONSE_INVALID"
    finally:
        await provider.aclose()


async def test_http_failure_no_retry_redirect_or_raw_body_leak():
    requests = []
    def handler(request):
        requests.append(request)
        return httpx.Response(302, headers={"Location": "https://attacker.test"}, text="CANARY-secret-body")
    provider = implementation("provider").Provider(profile(stream_responses=False), SecretStr("fixture-key"),
                                                     transport=httpx.MockTransport(handler))
    try:
        with pytest.raises(AgentError) as caught:
            await provider.complete([{"role": "user", "content": "test"}], [TOOL])
        assert "CANARY" not in str(caught.value) and len(requests) == 1
    finally:
        await provider.aclose()


async def test_complete_stream_requires_done_and_finish_before_tools():
    chunks = [
        {"choices": [{"index": 0, "delta": {"role": "assistant", "tool_calls": [
            {"index": 0, "id": "call-1", "type": "function", "function": {"name": "corel_preview", "arguments": '{"target":'}}]}, "finish_reason": None}]},
        {"choices": [{"index": 0, "delta": {"tool_calls": [
            {"index": 0, "function": {"arguments": '"selection"}'}}]}, "finish_reason": "tool_calls"}]},
    ]
    body = "".join(f"data: {json.dumps(chunk)}\n\n" for chunk in chunks)
    for complete in (False, True):
        provider = implementation("provider").Provider(profile(), SecretStr("fixture-key"),
            transport=httpx.MockTransport(lambda request: httpx.Response(200,
                headers={"content-type": "text/event-stream"}, text=body + ("data: [DONE]\n\n" if complete else ""))))
        try:
            if complete:
                assert (await provider.complete([{"role": "user", "content": "test"}], [TOOL])).tool_calls[0].name == "corel_preview"
            else:
                with pytest.raises(AgentError):
                    await provider.complete([{"role": "user", "content": "test"}], [TOOL])
        finally:
            await provider.aclose()
