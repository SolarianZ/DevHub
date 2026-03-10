from __future__ import annotations

import math
from typing import Any


def require_non_empty_string(value: Any, name: str, *, error_message: str | None = None) -> str:
    """要求值必须为非空字符串。"""

    if not isinstance(value, str) or not value.strip():
        raise ValueError(error_message or f"{name} 不能为空。")
    return value


def require_optional_string(
    value: Any,
    name: str,
    *,
    allow_empty: bool = True,
    error_message: str | None = None,
) -> str | None:
    """要求值必须为可选字符串。"""

    if value is None:
        return None
    if not isinstance(value, str):
        raise ValueError(f"{name} 类型非法。")
    if not allow_empty and not value.strip():
        raise ValueError(error_message or f"{name} 不能为空。")
    return value


def require_bool(value: Any, name: str) -> bool:
    """要求值必须为布尔值。"""

    if not isinstance(value, bool):
        raise ValueError(f"{name} 必须为布尔值。")
    return value


def require_optional_bool(value: Any, name: str) -> bool | None:
    """要求值必须为可选布尔值。"""

    if value is None:
        return None
    return require_bool(value, name)


def require_optional_int_at_least(
    value: Any,
    minimum_value: int,
    error_message: str,
) -> int | None:
    """要求值必须为大于等于指定下限的可选整数。"""

    if value is None:
        return None
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum_value:
        raise ValueError(error_message)
    return value


def require_optional_int_in_range(
    value: Any,
    minimum_value: int,
    maximum_value: int,
    error_message: str,
) -> int | None:
    """要求值必须为位于指定区间内的可选整数。"""

    if value is None:
        return None
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum_value or value > maximum_value:
        raise ValueError(error_message)
    return value


def require_positive_number(value: Any, name: str) -> float | int:
    """要求值必须为正数。"""

    if not isinstance(value, int | float) or isinstance(value, bool) or not math.isfinite(float(value)) or value <= 0:
        raise ValueError(f"{name} 必须大于 0。")
    return value


def require_protocol_version(value: Any) -> int:
    """要求协议版本必须为当前支持的版本。"""

    if not isinstance(value, int) or isinstance(value, bool) or value != 1:
        raise ValueError("当前仅支持协议版本 1。")
    return value
