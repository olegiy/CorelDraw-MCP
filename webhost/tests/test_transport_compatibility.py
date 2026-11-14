import asyncio
import base64

from conftest import implementation


async def test_node_v2_catalog_and_structured_content(transport_factory):
    transport = await transport_factory()
    try:
        catalog = await transport.session.list_tools()
        names = {tool.name for tool in catalog.tools}
        assert len(names) == 37
        assert {'corel_get_job', 'corel_import', 'corel_layout_shapes', 'corel_edit_text'} <= names
        assert not {'corel_execute_vba', 'corel_build_vba'} & names
        result = await transport.session.call_tool("corel_status", {})
        report = result.structuredContent
        assert report["job_id"] and report["tool"] == "corel_status"
        result = await transport.session.call_tool("corel_preview", {"document_id": "fixture"})
        job_id = result.structuredContent["job_id"]
        for _ in range(30):
            result = await transport.session.call_tool("corel_get_job", {
                "job_id": job_id, "include_preview": True, "wait_seconds": 0.1,
            })
            if result.structuredContent["status"] == "succeeded":
                break
            await asyncio.sleep(0.05)
        image = next(item for item in result.content if item.type == "image")
        assert image.mimeType == "image/png"
        assert base64.b64decode(image.data).startswith(b"\x89PNG\r\n\x1a\n")
    finally:
        await transport.aclose()


async def test_unknown_sdk_blocks_before_spawn(tmp_path, monkeypatch):
    owned = implementation("owned_stdio")
    monkeypatch.setattr(owned, "sdk_version", lambda: "2.0.0")
    transport = owned.OwnedStdio(command="node", args=[], directory=tmp_path)
    try:
        await transport.start()
    except owned.LifecycleError as error:
        assert error.code == "SDK_UNSUPPORTED"
    else:
        raise AssertionError("Unsupported SDK must not start a process")
    assert not (tmp_path / "mcp-owner.json").exists()
