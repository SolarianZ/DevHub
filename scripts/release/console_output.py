from __future__ import annotations

import sys
from typing import TextIO


def write_console_text(text: str, *, file: TextIO | None = None) -> None:
    stream = sys.stdout if file is None else file

    try:
        stream.write(text)
    except UnicodeEncodeError:
        encoding = getattr(stream, "encoding", None) or "utf-8"
        encoded = text.encode(encoding, errors="replace")
        buffer = getattr(stream, "buffer", None)
        if buffer is not None:
            buffer.write(encoded)
        else:
            stream.write(encoded.decode(encoding, errors="replace"))

    stream.flush()


def console_print(
    *values: object,
    sep: str = " ",
    end: str = "\n",
    file: TextIO | None = None,
) -> None:
    write_console_text(sep.join(str(value) for value in values) + end, file=file)
