import base64
import io

import pytest
from conftest import implementation
from PIL import Image

from corel_agent.contracts import AgentError, ImageBlock


def png(width=10, height=10):
    stream = io.BytesIO()
    Image.new("RGB", (width, height), "white").save(stream, format="PNG")
    return base64.b64encode(stream.getvalue()).decode()


async def test_preview_is_registered_by_opaque_id_not_report_path(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    image = await assets.save_preview(ImageBlock(mime_type="image/png", data=png()),
                                      document_id="exact-document", target="selection")
    data, metadata = await assets.read_image(image.image_id)
    assert data.startswith(b"\x89PNG") and metadata["document_id"] == "exact-document"
    assert image.width == 10 and image.target == "selection"
    with pytest.raises(AgentError):
        await assets.read_image("../../private.cdr")


@pytest.mark.parametrize("mime,data", [
    ("image/jpeg", png()), ("image/png", "not base64"),
    ("image/png", base64.b64encode(b"\x89PNG\r\n\x1a\nnot a PNG").decode()),
    ("image/png", png(2401, 1)),
    ("image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/vYEAAAAASUVORK5CYII="),
])
async def test_invalid_png_is_not_published(store, tmp_path, mime, data):
    assets = implementation("assets").Assets(tmp_path, store)
    with pytest.raises(AgentError) as caught:
        await assets.save_preview(ImageBlock(mime_type=mime, data=data), document_id="fixture")
    assert caught.value.code == "PREVIEW_INVALID"
    assert await store.list_assets() == []


async def test_modified_asset_fails_closed(store, tmp_path):
    assets = implementation("assets").Assets(tmp_path, store)
    image = await assets.save_preview(ImageBlock(mime_type="image/png", data=png()),
                                      document_id="fixture")
    (tmp_path / "assets" / f"{image.image_id}.png").write_bytes(b"replaced")
    with pytest.raises(AgentError):
        await assets.read_image(image.image_id)
