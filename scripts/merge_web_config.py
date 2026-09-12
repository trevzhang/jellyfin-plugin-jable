#!/usr/bin/env python3
"""Atomically add the Jable menu link to a Jellyfin Web config."""

import argparse
import json
import os
from pathlib import Path
import tempfile

MENU_LINK = {"name": "Jable", "icon": "search", "url": "/Jable/Page"}


def merge(source, destination):
    config = json.loads(Path(source).read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("config must be a JSON object")
    links = config.get("menuLinks", [])
    if not isinstance(links, list) or any(not isinstance(link, dict) for link in links):
        raise ValueError("menuLinks must be an array of objects")
    config["menuLinks"] = [link for link in links if link.get("url") != MENU_LINK["url"]] + [MENU_LINK]
    destination = Path(destination)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=destination.parent,
                                         prefix="." + destination.name + ".", delete=False) as output:
            temporary = Path(output.name)
            if destination.exists():
                os.fchmod(output.fileno(), destination.stat().st_mode & 0o7777)
            json.dump(config, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, destination)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def self_test():
    with tempfile.TemporaryDirectory() as directory:
        source = Path(directory) / "stock.json"
        destination = Path(directory) / "config.json"
        other = {"name": "Other", "url": "https://example.com"}
        original = {"theme": "dark", "nested": {"keep": True}, "menuLinks": [
            other, {"name": "Old", "url": "/Jable/Page"}, dict(MENU_LINK)]}
        source.write_text(json.dumps(original), encoding="utf-8")
        merge(source, destination)
        assert destination.exists(), "merge must write a config"
        destination.chmod(0o640)
        merge(destination, destination)
        assert destination.stat().st_mode & 0o777 == 0o640, "merge must preserve destination mode"
        assert json.loads(destination.read_text(encoding="utf-8")) == {
            "theme": "dark", "nested": {"keep": True}, "menuLinks": [other, MENU_LINK]}
        assert json.loads(source.read_text(encoding="utf-8")) == original
        source.write_text("{}", encoding="utf-8")
        merge(source, destination)
        assert json.loads(destination.read_text(encoding="utf-8")) == {"menuLinks": [MENU_LINK]}
        before = destination.read_bytes()
        for invalid in ("{", "[]", '{"menuLinks":{}}', '{"menuLinks":[null]}'):
            source.write_text(invalid, encoding="utf-8")
            try:
                merge(source, destination)
            except ValueError:
                pass
            else:
                raise AssertionError("invalid config must be rejected")
            assert destination.read_bytes() == before
    print("merge_web_config self-test passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?")
    parser.add_argument("destination", nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.source and args.destination:
        merge(args.source, args.destination)
    else:
        parser.error("provide source and destination, or --self-test")
