"""DevHub 协议定义的事件类型常量。"""

APP_INSTANCE_REGISTERED = "app.instance.registered"
APP_INSTANCE_UNREGISTERED = "app.instance.unregistered"
INVOCATION_QUEUED = "invocation.queued"
INVOCATION_DELIVERED = "invocation.delivered"
INVOCATION_COMPLETED = "invocation.completed"
INVOCATION_FAILED = "invocation.failed"

ALL_EVENT_TYPES = {
    APP_INSTANCE_REGISTERED,
    APP_INSTANCE_UNREGISTERED,
    INVOCATION_QUEUED,
    INVOCATION_DELIVERED,
    INVOCATION_COMPLETED,
    INVOCATION_FAILED,
}
