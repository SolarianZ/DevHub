from __future__ import annotations

import json
from typing import Any


def load_json_text(text: str, *, source: str) -> Any:
    """以严格 JSON 语义解析文本。"""

    try:
        return json.loads(text, parse_constant=_reject_non_standard_constant)
    except (json.JSONDecodeError, ValueError) as exc:
        raise RuntimeError(f"{source} 不是合法 JSON。") from exc


def _reject_non_standard_constant(value: str) -> None:
    raise ValueError(f"JSON 不支持常量 {value}。")
