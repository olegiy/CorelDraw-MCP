"""Offline schema export: importing/building the app never enters its lifespan."""

import argparse
import json
from pathlib import Path

from .app import create_app


def schema():
    return create_app().openapi()


def main():
    parser = argparse.ArgumentParser(description="Offline OpenAPI панели CorelDRAW")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    path = Path(__file__).resolve().parents[1] / "openapi.json"
    text = json.dumps(schema(), ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    if args.check:
        if not path.exists() or path.read_text(encoding="utf-8") != text:
            parser.exit(1, "OpenAPI drift: обновите offline схему и TS types.\n")
    else:
        path.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
