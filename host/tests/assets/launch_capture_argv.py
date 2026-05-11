#!/usr/bin/env python3
import json
import pathlib
import sys


pathlib.Path(sys.argv[1]).write_text(
    json.dumps(sys.argv[2:], ensure_ascii=False),
    encoding="utf-8",
)
