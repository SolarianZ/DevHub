from datetime import datetime
from typing import get_type_hints

from devhub_sdk import (
    ALL_EVENT_TYPES,
    APP_DEFINITION_DELETED,
    APP_DEFINITION_UPSERTED,
    DefinitionValidationResult,
    DevHubClientDependencies,
    DevHubEventType,
    DevHubClient,
    DevHubEventsClientDependencies,
    FileSystemRuntimeResolver,
    JsonRpcHttpTransport,
    JsonRpcWsSession,
    RuntimeResolver,
    SUPPORTED_EVENT_TYPES,
    UrllibJsonRpcHttpTransport,
    ValidationIssue,
    WebSocketJsonRpcSession,
    ensure_supported_event_type,
    resolve_data_directory,
)
from devhub_sdk.client import DevHubClientDependencies as InternalDevHubClientDependencies
from devhub_sdk.events import DevHubEventsClientDependencies as InternalDevHubEventsClientDependencies
from devhub_sdk.constants import ALL_EVENT_TYPES as InternalAllEventTypes
from devhub_sdk.constants import APP_DEFINITION_DELETED as InternalAppDefinitionDeleted
from devhub_sdk.constants import APP_DEFINITION_UPSERTED as InternalAppDefinitionUpserted
from devhub_sdk.constants import DevHubEventType as InternalDevHubEventType
from devhub_sdk.constants import SUPPORTED_EVENT_TYPES as InternalSupportedEventTypes
from devhub_sdk.constants import ensure_supported_event_type as InternalEnsureSupportedEventType
from devhub_sdk._http_transport import JsonRpcHttpTransport as InternalJsonRpcHttpTransport
from devhub_sdk._http_transport import UrllibJsonRpcHttpTransport as InternalUrllibJsonRpcHttpTransport
from devhub_sdk._ws_session import JsonRpcWsSession as InternalJsonRpcWsSession
from devhub_sdk._ws_session import WebSocketJsonRpcSession as InternalWebSocketJsonRpcSession
from devhub_sdk.models import DefinitionValidationResult as InternalDefinitionValidationResult
from devhub_sdk.models import ValidationIssue as InternalValidationIssue
from devhub_sdk.runtime import FileSystemRuntimeResolver as InternalFileSystemRuntimeResolver
from devhub_sdk.runtime import RuntimeResolver as InternalRuntimeResolver
from devhub_sdk.runtime import resolve_data_directory as InternalResolveDataDirectory


def test_package_root_should_export_runtime_and_transport_abstractions() -> None:
    assert APP_DEFINITION_UPSERTED is InternalAppDefinitionUpserted
    assert APP_DEFINITION_DELETED is InternalAppDefinitionDeleted
    assert DevHubClientDependencies is InternalDevHubClientDependencies
    assert DevHubEventsClientDependencies is InternalDevHubEventsClientDependencies
    assert DefinitionValidationResult is InternalDefinitionValidationResult
    assert RuntimeResolver is InternalRuntimeResolver
    assert FileSystemRuntimeResolver is InternalFileSystemRuntimeResolver
    assert resolve_data_directory is InternalResolveDataDirectory
    assert DevHubEventType is InternalDevHubEventType
    assert SUPPORTED_EVENT_TYPES is InternalSupportedEventTypes
    assert ALL_EVENT_TYPES is InternalAllEventTypes
    assert ValidationIssue is InternalValidationIssue
    assert ensure_supported_event_type is InternalEnsureSupportedEventType
    assert JsonRpcHttpTransport is InternalJsonRpcHttpTransport
    assert UrllibJsonRpcHttpTransport is InternalUrllibJsonRpcHttpTransport
    assert JsonRpcWsSession is InternalJsonRpcWsSession
    assert WebSocketJsonRpcSession is InternalWebSocketJsonRpcSession


def test_public_client_api_should_preserve_heartbeat_return_type_annotation() -> None:
    assert get_type_hints(DevHubClient.heartbeat)["return"] is datetime
