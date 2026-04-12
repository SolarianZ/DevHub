"""DevHub 协议定义的事件类型。"""

from __future__ import annotations

from enum import StrEnum


class DevHubEventType(StrEnum):
    """DevHub 对外公开的事件类型闭集。"""

    APP_DEFINITION_UPSERTED = "app.definition.upserted"
    APP_DEFINITION_DELETED = "app.definition.deleted"
    APP_INSTANCE_REGISTERED = "app.instance.registered"
    APP_INSTANCE_UNREGISTERED = "app.instance.unregistered"
    INVOCATION_QUEUED = "invocation.queued"
    INVOCATION_DELIVERED = "invocation.delivered"
    INVOCATION_COMPLETED = "invocation.completed"
    INVOCATION_FAILED = "invocation.failed"


APP_DEFINITION_UPSERTED = DevHubEventType.APP_DEFINITION_UPSERTED
APP_DEFINITION_DELETED = DevHubEventType.APP_DEFINITION_DELETED
APP_INSTANCE_REGISTERED = DevHubEventType.APP_INSTANCE_REGISTERED
APP_INSTANCE_UNREGISTERED = DevHubEventType.APP_INSTANCE_UNREGISTERED
INVOCATION_QUEUED = DevHubEventType.INVOCATION_QUEUED
INVOCATION_DELIVERED = DevHubEventType.INVOCATION_DELIVERED
INVOCATION_COMPLETED = DevHubEventType.INVOCATION_COMPLETED
INVOCATION_FAILED = DevHubEventType.INVOCATION_FAILED

SUPPORTED_EVENT_TYPES: tuple[DevHubEventType, ...] = (
    APP_DEFINITION_UPSERTED,
    APP_DEFINITION_DELETED,
    APP_INSTANCE_REGISTERED,
    APP_INSTANCE_UNREGISTERED,
    INVOCATION_QUEUED,
    INVOCATION_DELIVERED,
    INVOCATION_COMPLETED,
    INVOCATION_FAILED,
)

ALL_EVENT_TYPES = frozenset(SUPPORTED_EVENT_TYPES)


def ensure_supported_event_type(value: object, property_name: str) -> DevHubEventType:
    """将输入收敛为受支持的事件类型。"""

    if isinstance(value, DevHubEventType):
        return value
    if isinstance(value, str):
        try:
            return DevHubEventType(value)
        except ValueError as exc:
            raise ValueError(f"{property_name} 必须为受支持的 DevHub 事件类型。") from exc
    raise ValueError(f"{property_name} 必须为受支持的 DevHub 事件类型。")
