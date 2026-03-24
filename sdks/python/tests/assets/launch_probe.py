#!/usr/bin/env python3
from __future__ import annotations

import os
import sys
import time
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("用法：launch_probe.py <ready-file>")

    ready_file = Path(sys.argv[1])
    ready_file.parent.mkdir(parents=True, exist_ok=True)
    ready_file.write_text(str(os.getpid()), encoding="utf-8")

    deadline = time.time() + 60
    while time.time() < deadline:
        time.sleep(0.1)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
