from copy import deepcopy

from mcp.types import CallToolResult, ImageContent, TextContent

from corel_agent.contracts import new_id


def report(status="running", *, tool="corel_execute_vba", **updates):
    return {"job_id": new_id(), "owner_pid": 123, "owner_session": "fixture",
            "request_id": "web:fixture", "fingerprint": "f" * 64, "tool": tool,
            "status": status, "stage": "fixture", "started_at": "2026-11-04T00:00:00Z",
            "deadline_at": "2026-11-04T00:10:00Z", "deadline_exceeded": False,
            "run_directory": "C:\\fixture\\runs", "report_path": "C:\\fixture\\report.json",
            "log_path": "C:\\fixture\\run.log", "artifacts": [], "warnings": [],
            "cleanup": {"completed": True, "errors": []}, **updates}


class ScriptedSession:
    def __init__(self, statuses=("running", "succeeded"), *, tool="corel_execute_vba",
                 fail_dispatch=False, fail_poll=False, cleanup=None, image=None):
        self.base = report(tool=tool)
        self.statuses, self.calls = list(statuses), []
        self.fail_dispatch, self.fail_poll = fail_dispatch, fail_poll
        self.cleanup, self.image = cleanup, image

    async def call_tool(self, name, arguments=None, **kwargs):
        self.calls.append((name, deepcopy(arguments)))
        if name != "corel_get_job" and self.fail_dispatch:
            raise ConnectionError("canary raw transport error")
        if name == "corel_get_job" and self.fail_poll:
            self.fail_poll = False
            raise TimeoutError("Synthetic read timeout")
        status = self.statuses.pop(0) if len(self.statuses) > 1 else self.statuses[0]
        data = {**self.base, "status": status, "result": {"applied": True}}
        if self.cleanup is not None:
            data["cleanup"] = self.cleanup
        blocks = [TextContent(type="text", text="{}")]
        if name == "corel_get_job" and arguments.get("include_preview") and self.image:
            blocks.append(ImageContent(type="image", mimeType="image/png", data=self.image))
        return CallToolResult(content=blocks, structuredContent=data,
                              isError=data["cleanup"].get("completed") is False)
