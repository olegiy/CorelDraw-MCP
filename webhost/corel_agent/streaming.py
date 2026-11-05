"""Bounded native completion assembly; no partial tool call is executable."""

import json


def strict_json(text):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON member")
            result[key] = value
        return result
    def invalid(value):
        raise ValueError("Non-finite JSON")
    return json.loads(text, object_pairs_hook=pairs, parse_constant=invalid)


class CompletionStream:
    def __init__(self):
        self.text, self.calls = [], {}
        self.finish = None
        self.done = False
        self.size = 0

    def feed(self, event):
        self.size += len(event.encode("utf-8"))
        if self.done or self.size > 8 * 1024**2:
            raise ValueError("Stream limit")
        if event == "[DONE]":
            self.done = True
            return ""
        data = strict_json(event)
        if not isinstance(data, dict) or "error" in data:
            raise ValueError("Invalid stream")
        choices = data.get("choices")
        if choices == [] and isinstance(data.get("usage"), dict):
            return ""
        if not isinstance(choices, list) or len(choices) != 1:
            raise ValueError("Invalid choices")
        choice = choices[0]
        if choice.get("index", 0) != 0:
            raise ValueError("Invalid choice index")
        delta = choice.get("delta")
        if not isinstance(delta, dict) or (self.finish is not None and delta):
            raise ValueError("Invalid delta")
        if delta.get("role", "assistant") != "assistant":
            raise ValueError("Invalid role")
        text = delta.get("content") or ""
        if not isinstance(text, str):
            raise ValueError("Invalid content")
        self.text.append(text)
        fragments = delta.get("tool_calls") or []
        if not isinstance(fragments, list) or len(fragments) > 100:
            raise ValueError("Invalid calls")
        for fragment in fragments:
            index = fragment.get("index")
            if type(index) is not int or not 0 <= index < 100:
                raise ValueError("Invalid call index")
            call = self.calls.setdefault(index, {"id": "", "type": "function", "function": {
                "name": "", "arguments": ""}})
            if fragment.get("type", "function") != "function":
                raise ValueError("Invalid call type")
            function = fragment.get("function", {})
            for owner, key, value in [(call, "id", fragment.get("id", "")),
                                      (call["function"], "name", function.get("name", "")),
                                      (call["function"], "arguments", function.get("arguments", ""))]:
                if not isinstance(value, str):
                    raise ValueError("Invalid fragment")
                owner[key] += value
        finish = choice.get("finish_reason")
        if finish is not None:
            if self.finish is not None:
                raise ValueError("Repeated finish")
            self.finish = finish
        return text

    def result(self):
        if not self.done or self.finish not in {"stop", "tool_calls"} or sorted(self.calls) != list(range(len(self.calls))):
            raise ValueError("Incomplete stream")
        return {"choices": [{"finish_reason": self.finish, "message": {
            "role": "assistant", "content": "".join(self.text) or None,
            "tool_calls": [self.calls[index] for index in sorted(self.calls)],
        }}]}
