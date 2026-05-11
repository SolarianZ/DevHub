from __future__ import annotations

import pytest

from devhub_sdk import AppDefinition, AppInstanceRegistration, InvokeCapability, InvocationTarget, LaunchConfiguration
from devhub_sdk.models import (
    InvokeRequest,
    LaunchRequest,
    ListDefinitionsRequest,
    ListInstancesRequest,
    PollRequest,
    RespondRequest,
)


def test_public_models_should_preserve_case_sensitive_canonical_identifiers() -> None:
    definition = AppDefinition(
        app_id="Sample.App",
        display_name="Sample",
        scope="Workspace-A.v2",
        launch=LaunchConfiguration(args=["--scope", "{scope}"]),
    )
    instance = AppInstanceRegistration(
        instance_id="NODE_01.alpha",
        app_id="Sample.App",
        pid=1,
        invoke=InvokeCapability(poll=True, respond=True),
        scope="Workspace-A.v2",
    )
    target = InvocationTarget(scope="Workspace-A.v2", instance_id="NODE_01.alpha")
    launch = LaunchRequest(app_id="Sample.App", scope="Workspace-A.v2")
    list_definitions = ListDefinitionsRequest(scope="Workspace-A.v2", app_id="Sample.App")
    list_instances = ListInstancesRequest(scope=None, app_id="Sample.App")
    invoke = InvokeRequest(app_id="Sample.App", method="test.request", target=target)
    poll = PollRequest(instance_id="NODE_01.alpha", instance_session_token="token-1")
    respond = RespondRequest(
        instance_id="NODE_01.alpha",
        instance_session_token="token-1",
        invocation_id="invk-1",
        lease_token="lease-1",
        value={"ok": True},
    )

    assert definition.app_id == "Sample.App"
    assert definition.scope == "Workspace-A.v2"
    assert definition.launch is not None
    assert definition.launch.args == ["--scope", "{scope}"]
    assert instance.instance_id == "NODE_01.alpha"
    assert instance.app_id == "Sample.App"
    assert target.scope == "Workspace-A.v2"
    assert target.instance_id == "NODE_01.alpha"
    assert launch.app_id == "Sample.App"
    assert list_definitions.app_id == "Sample.App"
    assert list_instances.app_id == "Sample.App"
    assert invoke.app_id == "Sample.App"
    assert poll.instance_id == "NODE_01.alpha"
    assert respond.instance_id == "NODE_01.alpha"


@pytest.mark.parametrize(
    "factory",
    [
        lambda: AppDefinition(app_id=".Sample", display_name="Sample", scope=""),
        lambda: AppDefinition(app_id="Sample.App", display_name=" ", scope=""),
        lambda: AppInstanceRegistration(
            instance_id="NODE_01.alpha-",
            app_id="Sample.App",
            pid=1,
            invoke=InvokeCapability(poll=True, respond=True),
            scope="",
        ),
        lambda: InvocationTarget(scope=".workspace"),
        lambda: LaunchRequest(app_id="Sample.App", scope="workspace."),
        lambda: ListDefinitionsRequest(scope=None, app_id="bad app"),
        lambda: ListInstancesRequest(scope="-workspace", app_id=None),
        lambda: InvokeRequest(app_id="Sample.App-", method="test.request", target=InvocationTarget(scope="")),
        lambda: PollRequest(instance_id="inst:1", instance_session_token="token-1"),
        lambda: RespondRequest(
            instance_id=".inst-1",
            instance_session_token="token-1",
            invocation_id="invk-1",
            lease_token="lease-1",
            value={"ok": True},
        ),
    ],
)
def test_public_models_should_reject_invalid_identifiers(factory) -> None:
    with pytest.raises(ValueError):
        factory()
