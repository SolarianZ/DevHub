from __future__ import annotations

from hashlib import sha256
from uuid import uuid4

from ._validation import require_app_id, require_instance_id, require_scoped_string


def create_instance_id(app_id: str, scope: str = "") -> str:
    """生成适合 Hub 全局注册表使用的实例标识。"""

    normalized_app_id = require_app_id(app_id, "app_id")
    normalized_scope = require_scoped_string(scope, "scope")
    scope_segment = normalized_scope if normalized_scope else "global"
    random_suffix = uuid4().hex
    readable = f"{normalized_app_id}.{scope_segment}.py-{random_suffix}"
    if len(readable) <= 256:
        return require_instance_id(readable, "instance_id")

    identity_digest = sha256(f"{normalized_app_id}\0{normalized_scope}".encode("utf-8")).hexdigest()[:24]
    return require_instance_id(f"py.{identity_digest}.{random_suffix}", "instance_id")
