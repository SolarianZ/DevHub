import {
  APP_DEFINITION_DELETED,
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  DevHubClient,
  DevHubEventsClient,
  type AppDefinition,
  type AppDefinitionIdentity,
  type AppInstance,
} from "@devhub/sdk";
import { startTransition, useEffect, useEffectEvent, useRef, useState } from "react";
import { createStaticRuntimeResolver, resumeDiscovery } from "../lib/monitor-api";
import type { BootstrapSnapshot, FrontendLogInput } from "../lib/models";
import {
  type HostSessionStatus,
  disposeSessionResources,
  getRuntimePort,
  removeDefinition,
  shouldRecoverHostSession,
  sortDefinitions,
  sortInstances,
  toErrorMessage,
  upsertDefinition,
} from "../lib/monitor-ui";
import { listAllDefinitions } from "../lib/sdk-compat";

const HTTP_CLIENT_ID = "devhub-monitor-ui";
const EVENTS_CLIENT_ID = "devhub-monitor-ui-events";
const INSTANCE_REFRESH_EVENT_TYPES = [APP_INSTANCE_REGISTERED, APP_INSTANCE_UNREGISTERED] as const;
const DEFINITION_REFRESH_EVENT_TYPES = [APP_DEFINITION_UPSERTED, APP_DEFINITION_DELETED] as const;

interface HostSessionOptions {
  bootstrap: BootstrapSnapshot | null;
  onReplaceBootstrap: (snapshot: BootstrapSnapshot) => void;
  recordFrontendLog: (entry: FrontendLogInput) => void;
}

export function useHostSession(options: HostSessionOptions) {
  const { bootstrap, onReplaceBootstrap, recordFrontendLog } = options;

  const [definitions, setDefinitions] = useState<AppDefinition[]>([]);
  const [instances, setInstances] = useState<AppInstance[]>([]);
  const [hostSessionStatus, setHostSessionStatus] = useState<HostSessionStatus>("idle");
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [sessionResetVersion, setSessionResetVersion] = useState(0);

  const recoveryInFlightRef = useRef(false);
  const hostClientRef = useRef<DevHubClient | null>(null);
  const eventsClientRef = useRef<DevHubEventsClient | null>(null);
  const subscriptionIdRef = useRef<string | null>(null);

  const disposeHostSession = useEffectEvent(async () => {
    const hostClient = hostClientRef.current;
    const eventsClient = eventsClientRef.current;
    const subscriptionId = subscriptionIdRef.current;

    hostClientRef.current = null;
    eventsClientRef.current = null;
    subscriptionIdRef.current = null;

    await disposeSessionResources(hostClient, eventsClient, subscriptionId);
  });

  const handleConnectionLoss = useEffectEvent(async (reason: string, lossError?: unknown) => {
    if (recoveryInFlightRef.current) {
      return;
    }

    recoveryInFlightRef.current = true;

    recordFrontendLog({
      level: "warn",
      category: "frontend.connection",
      action: "disconnect",
      result: "recovering",
      message: toErrorMessage(lossError),
      context: {
        reason,
      },
    });

    startTransition(() => {
      setDefinitions([]);
      setInstances([]);
      setHostSessionStatus("recovering");
      setSessionError(null);
      setSessionResetVersion((current) => current + 1);
    });

    await disposeHostSession();

    try {
      const snapshot = await resumeDiscovery(reason);
      onReplaceBootstrap(snapshot);
    } catch (resumeError) {
      setSessionError(toErrorMessage(resumeError));
    } finally {
      recoveryInFlightRef.current = false;
    }
  });

  const runHostAction = useEffectEvent(async <T,>(
    action: string,
    execute: (client: DevHubClient) => Promise<T>,
  ): Promise<T> => {
    const client = hostClientRef.current;
    if (!client) {
      throw new Error("当前没有可用的 DevHub Host 会话。");
    }

    try {
      return await execute(client);
    } catch (hostError) {
      if (shouldRecoverHostSession(hostError)) {
        await handleConnectionLoss(action, hostError);
      }

      throw hostError;
    }
  });

  const replaceDefinitionInState = useEffectEvent((definition: AppDefinition) => {
    startTransition(() => {
      setDefinitions((current) => upsertDefinition(current, definition));
    });
  });

  const removeDefinitionFromState = useEffectEvent((identity: AppDefinitionIdentity) => {
    startTransition(() => {
      setDefinitions((current) => removeDefinition(current, identity));
    });
  });

  useEffect(() => {
    if (!bootstrap?.connection || bootstrap.phase !== "host_available") {
      startTransition(() => {
        setDefinitions([]);
        setInstances([]);
        setHostSessionStatus((current) => (current === "recovering" ? current : "idle"));
        setSessionError(null);
      });
      void disposeHostSession();
      return;
    }

    let disposed = false;
    let localHostClient: DevHubClient | null = null;
    let localEventsClient: DevHubEventsClient | null = null;
    let localSubscriptionId: string | null = null;
    const connection = bootstrap.connection;
    const runtimeResolver = createStaticRuntimeResolver(connection);

    async function refreshDefinitions(trigger: string, hostClient: DevHubClient): Promise<void> {
      try {
        const refreshedDefinitions = await listAllDefinitions(hostClient);
        if (disposed) {
          return;
        }

        startTransition(() => {
          setDefinitions(sortDefinitions(refreshedDefinitions));
          setSessionError(null);
        });

        recordFrontendLog({
          level: "info",
          category: "frontend.inventory",
          action: "refresh_definitions",
          result: trigger,
          context: {
            trigger,
          },
        });
      } catch (refreshError) {
        if (shouldRecoverHostSession(refreshError)) {
          await handleConnectionLoss(`refresh_definitions_${trigger}`, refreshError);
          return;
        }

        setSessionError(toErrorMessage(refreshError));
      }
    }

    async function refreshInstances(trigger: string, hostClient: DevHubClient): Promise<void> {
      try {
        const refreshedInstances = await hostClient.listInstances({
          scope: null,
          includeOffline: true,
        });
        if (disposed) {
          return;
        }

        startTransition(() => {
          setInstances(sortInstances(refreshedInstances));
          setSessionError(null);
        });

        recordFrontendLog({
          level: "info",
          category: "frontend.inventory",
          action: "refresh_instances",
          result: trigger,
          context: {
            trigger,
          },
        });
      } catch (refreshError) {
        if (shouldRecoverHostSession(refreshError)) {
          await handleConnectionLoss(`refresh_instances_${trigger}`, refreshError);
          return;
        }

        setSessionError(toErrorMessage(refreshError));
      }
    }

    startTransition(() => {
      setHostSessionStatus("connecting");
      setSessionError(null);
    });

    async function connectHostSession() {
      try {
        const [hostClient, eventsClient] = await Promise.all([
          DevHubClient.fromRuntime(
            {
              clientId: HTTP_CLIENT_ID,
              requestTimeoutMs: 3_000,
            },
            {
              runtimeResolver,
            },
          ),
          DevHubEventsClient.fromRuntime(
            {
              clientId: EVENTS_CLIENT_ID,
              requestTimeoutMs: 3_000,
            },
            {
              runtimeResolver,
            },
          ),
        ]);

        if (disposed) {
          await disposeSessionResources(hostClient, eventsClient, null);
          return;
        }

        localHostClient = hostClient;
        localEventsClient = eventsClient;

        await eventsClient.authenticate();
        localSubscriptionId = await eventsClient.subscribe([
          ...DEFINITION_REFRESH_EVENT_TYPES,
          ...INSTANCE_REFRESH_EVENT_TYPES,
        ]);

        const [nextDefinitions, nextInstances] = await Promise.all([
          listAllDefinitions(hostClient),
          hostClient.listInstances({
            scope: null,
            includeOffline: true,
          }),
        ]);

        if (disposed) {
          await disposeSessionResources(hostClient, eventsClient, localSubscriptionId);
          return;
        }

        hostClientRef.current = hostClient;
        eventsClientRef.current = eventsClient;
        subscriptionIdRef.current = localSubscriptionId;

        startTransition(() => {
          setDefinitions(sortDefinitions(nextDefinitions));
          setInstances(sortInstances(nextInstances));
          setHostSessionStatus("connected");
          setSessionError(null);
        });

        recordFrontendLog({
          level: "info",
          category: "frontend.connection",
          action: "connect",
          result: "connected",
          message: "DevHub Host session connected.",
          context: {
            port: getRuntimePort(connection),
            runtimeDirectory: connection.runtimeDirectory,
          },
        });

        for await (const event of eventsClient.readEvents()) {
          if (disposed) {
            return;
          }

          if (event.type === APP_DEFINITION_UPSERTED || event.type === APP_DEFINITION_DELETED) {
            await refreshDefinitions(event.type, hostClient);
            continue;
          }

          if (event.type === APP_INSTANCE_REGISTERED || event.type === APP_INSTANCE_UNREGISTERED) {
            await refreshInstances(event.type, hostClient);
          }
        }

        throw new Error("Host 事件流已终止。");
      } catch (connectError) {
        if (!disposed) {
          await handleConnectionLoss("host_session_terminated", connectError);
        }
      }
    }

    void connectHostSession();

    return () => {
      disposed = true;

      if (hostClientRef.current === localHostClient) {
        hostClientRef.current = null;
      }
      if (eventsClientRef.current === localEventsClient) {
        eventsClientRef.current = null;
      }
      if (subscriptionIdRef.current === localSubscriptionId) {
        subscriptionIdRef.current = null;
      }

      void disposeSessionResources(localHostClient, localEventsClient, localSubscriptionId);
    };
  }, [
    bootstrap?.connection?.rpcEndpoint,
    bootstrap?.connection?.runtime.pid,
    bootstrap?.generation,
    bootstrap?.phase,
  ]);

  return {
    definitions,
    disposeHostSession,
    hostSessionStatus,
    instances,
    removeDefinitionFromState,
    replaceDefinitionInState,
    runHostAction,
    sessionError,
    sessionResetVersion,
  };
}
