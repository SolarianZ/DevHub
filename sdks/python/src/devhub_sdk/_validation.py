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


def ensure_json_value(value: Any, property_name: str) -> Any:
    """校验并规范化任意 JSON 值。"""

    return _validate_json_value(value, property_name, set())


def ensure_json_object(value: Any, property_name: str) -> dict[str, Any]:
    """校验并规范化 JSON 对象。"""

    parsed = ensure_json_value(value, property_name)
    if not isinstance(parsed, dict):
        raise ValueError(f"{property_name} 必须为 JSON 对象。")
    return parsed


def _validate_json_value(value: Any, path: str, ancestors: set[int]) -> Any:
    if value is None or isinstance(value, bool | str):
        return value

    if isinstance(value, int):
        return value

    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError(f"{path} 必须为有限数字。")
        return value

    if isinstance(value, list):
        return _validate_json_array(value, path, ancestors)

    if isinstance(value, dict):
        return _validate_json_object(value, path, ancestors)

    raise ValueError(f"{path} 包含不支持的 JSON 类型。")


def _validate_json_array(value: list[Any], path: str, ancestors: set[int]) -> list[Any]:
    value_id = id(value)
    if value_id in ancestors:
        raise ValueError(f"{path} 不能包含循环引用。")

    ancestors.add(value_id)
    try:
        return [_validate_json_value(item, f"{path}[{index}]", ancestors) for index, item in enumerate(value)]
    finally:
        ancestors.remove(value_id)


def _validate_json_object(value: dict[Any, Any], path: str, ancestors: set[int]) -> dict[str, Any]:
    value_id = id(value)
    if value_id in ancestors:
        raise ValueError(f"{path} 不能包含循环引用。")

    ancestors.add(value_id)
    try:
        normalized: dict[str, Any] = {}
        for key, item in value.items():
            if not isinstance(key, str):
                raise ValueError(f"{path} 的对象键必须为字符串。")
            normalized[key] = _validate_json_value(item, f"{path}.{key}", ancestors)
        return normalized
    finally:
        ancestors.remove(value_id)
