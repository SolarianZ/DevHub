from datetime import datetime
from typing import get_type_hints

from devhub_sdk import (
    ALL_EVENT_TYPES,
    APP_DEFINITION_DELETED,
    APP_DEFINITION_UPSERTED,
    SDK_VERSION,
    AbandonedRequestFilter,
    AppInstance,
    DefinitionValidationResult,
    DevHubClientDependencies,
    DevHubEventType,
    DevHubClient,
    DevHubEventsClient,
    DevHubEventsClientDependencies,
    FileSystemRuntimeResolver,
    JsonRpcHttpTransport,
    JsonRpcWsSession,
    RuntimeResolver,
    SUPPORTED_EVENT_TYPES,
    UrllibJsonRpcHttpTransport,
    ValidationIssue,
    VersionCompatibilityResult,
    VersionCompatibilityStatus,
    WebSocketJsonRpcSession,
    __version__,
    create_instance_id,
    ensure_supported_event_type,
    resolve_data_directory,
)
from devhub_sdk.client import DevHubClientDependencies as InternalDevHubClientDependencies
from devhub_sdk.events import DevHubEventsClientDependencies as InternalDevHubEventsClientDependencies
from devhub_sdk.identity import create_instance_id as InternalCreateInstanceId
from devhub_sdk._versioning import SDK_VERSION as InternalSdkVersion
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
from devhub_sdk.models import AbandonedRequestFilter as InternalAbandonedRequestFilter
from devhub_sdk.models import DefinitionValidationResult as InternalDefinitionValidationResult
from devhub_sdk.models import ValidationIssue as InternalValidationIssue
from devhub_sdk.models import VersionCompatibilityResult as InternalVersionCompatibilityResult
from devhub_sdk.models import VersionCompatibilityStatus as InternalVersionCompatibilityStatus
from devhub_sdk.runtime import FileSystemRuntimeResolver as InternalFileSystemRuntimeResolver
from devhub_sdk.runtime import RuntimeResolver as InternalRuntimeResolver
from devhub_sdk.runtime import resolve_data_directory as InternalResolveDataDirectory


def test_package_root_should_export_runtime_and_transport_abstractions() -> None:
    assert APP_DEFINITION_UPSERTED is InternalAppDefinitionUpserted
    assert APP_DEFINITION_DELETED is InternalAppDefinitionDeleted
    assert AbandonedRequestFilter is InternalAbandonedRequestFilter
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
    assert VersionCompatibilityResult is InternalVersionCompatibilityResult
    assert VersionCompatibilityStatus is InternalVersionCompatibilityStatus
    assert SDK_VERSION == InternalSdkVersion
    assert __version__ == InternalSdkVersion
    assert create_instance_id is InternalCreateInstanceId


def test_public_client_api_should_preserve_heartbeat_return_type_annotation() -> None:
    assert get_type_hints(DevHubClient.heartbeat)["return"] is datetime


def test_public_client_api_should_preserve_exact_instance_return_type_annotations() -> None:
    assert get_type_hints(DevHubClient.get_instance)["return"] is AppInstance
    assert get_type_hints(DevHubEventsClient.get_instance)["return"] is AppInstance


def test_public_client_api_should_export_version_related_return_types() -> None:
    assert get_type_hints(DevHubClient.get_host_version)["return"] is str
    assert get_type_hints(DevHubEventsClient.get_host_version)["return"] is str
    assert get_type_hints(DevHubClient.check_version_compatibility)["return"] is VersionCompatibilityResult
    assert get_type_hints(DevHubEventsClient.check_version_compatibility)["return"] is VersionCompatibilityResult
