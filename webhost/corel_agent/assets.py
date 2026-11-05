"""Registered immutable preview bytes, never file paths received in an MCP report."""

import asyncio
import base64
import hashlib
import io
import os
import re
import stat
from pathlib import Path

from PIL import Image

from .contracts import AgentError, ImageData, new_id

MAX_PREVIEW_BYTES = 10 * 1024**2
ID_PATTERN = re.compile(r"^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$")


def assert_no_links(path: Path):
    path = Path(path).absolute()
    for part in [*reversed(path.parents), path]:
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise AgentError("PATH_DENIED", "Reparse point, junction и ссылки запрещены.", 403)


def decode_png(block):
    try:
        if block.mime_type != "image/png" or len(block.data) > ((MAX_PREVIEW_BYTES + 2) // 3) * 4:
            raise ValueError
        data = base64.b64decode(block.data, validate=True)
        if not 0 < len(data) <= MAX_PREVIEW_BYTES or not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError
        with Image.open(io.BytesIO(data)) as image:
            if image.format != "PNG" or not all(1 <= dimension <= 2400 for dimension in image.size):
                raise ValueError
            width, height = image.size
            image.verify()
        with Image.open(io.BytesIO(data)) as image:
            image.load()
        return data, width, height
    except (ValueError, OSError, SyntaxError, Image.DecompressionBombError):
        raise AgentError("PREVIEW_INVALID", "Превью не является допустимым PNG до 10 МиБ и 2400 px.", 422) from None


def write_bytes(path: Path, data: bytes):
    assert_no_links(path)
    temporary = path.with_name(f".{path.name}.{new_id()}.tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def read_verified(path: Path, digest: str, limit=MAX_PREVIEW_BYTES):
    assert_no_links(path)
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= limit:
            raise AgentError("ASSET_INVALID", "Недопустимый файл артефакта.", 409)
        data = stream.read(limit + 1)
        after = os.fstat(stream.fileno())
        if (before.st_size, before.st_mtime_ns, before.st_ino) != (after.st_size, after.st_mtime_ns, after.st_ino):
            raise AgentError("ASSET_CHANGED", "Артефакт изменился при чтении.")
    assert_no_links(path)
    if len(data) != before.st_size or hashlib.sha256(data).hexdigest() != digest:
        raise AgentError("ASSET_CHANGED", "Содержимое артефакта изменилось.")
    return data


class Assets:
    def __init__(self, directory: Path, store):
        self.directory = Path(directory) / "assets"
        self.store = store

    async def save_preview(self, block, *, document_id, page_index=None, target="page"):
        data, width, height = await asyncio.to_thread(decode_png, block)
        image = ImageData(image_id=new_id(), document_id=document_id, page_index=page_index,
                          target=target, width=width, height=height)
        def save():
            assert_no_links(self.directory)
            self.directory.mkdir(parents=True, exist_ok=True)
            write_bytes(self.directory / f"{image.image_id}.png", data)
        await asyncio.to_thread(save)
        await self.store.register_asset(image.image_id, "image", {
            **image.model_dump(), "sha256": hashlib.sha256(data).hexdigest(),
        })
        return image

    async def read_image(self, image_id):
        if not ID_PATTERN.fullmatch(image_id):
            raise AgentError("ASSET_NOT_FOUND", "Недопустимый image_id.", 404)
        metadata = await self.store.get_asset(image_id, "image")
        data = await asyncio.to_thread(read_verified, self.directory / f"{image_id}.png",
                                       metadata["sha256"])
        return data, metadata
