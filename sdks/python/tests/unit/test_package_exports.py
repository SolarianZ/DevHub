from devhub_sdk import (
    FileSystemRuntimeResolver,
    JsonRpcHttpTransport,
    JsonRpcWsSession,
    RuntimeResolver,
    UrllibJsonRpcHttpTransport,
    WebSocketJsonRpcSession,
)
from devhub_sdk._http_transport import JsonRpcHttpTransport as InternalJsonRpcHttpTransport
from devhub_sdk._http_transport import UrllibJsonRpcHttpTransport as InternalUrllibJsonRpcHttpTransport
from devhub_sdk._ws_session import JsonRpcWsSession as InternalJsonRpcWsSession
from devhub_sdk._ws_session import WebSocketJsonRpcSession as InternalWebSocketJsonRpcSession
from devhub_sdk.runtime import FileSystemRuntimeResolver as InternalFileSystemRuntimeResolver
from devhub_sdk.runtime import RuntimeResolver as InternalRuntimeResolver


def test_package_root_should_export_runtime_and_transport_abstractions() -> None:
    assert RuntimeResolver is InternalRuntimeResolver
    assert FileSystemRuntimeResolver is InternalFileSystemRuntimeResolver
    assert JsonRpcHttpTransport is InternalJsonRpcHttpTransport
    assert UrllibJsonRpcHttpTransport is InternalUrllibJsonRpcHttpTransport
    assert JsonRpcWsSession is InternalJsonRpcWsSession
    assert WebSocketJsonRpcSession is InternalWebSocketJsonRpcSession
