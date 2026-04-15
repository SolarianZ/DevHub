import { startTransition, useEffect, useEffectEvent, useState } from "react";
import { listLogs, readLog } from "../lib/monitor-api";
import type { LogFileInfo, LogKind, LogReadResult } from "../lib/models";
import type { RoutePage } from "../lib/monitor-ui";
import { toErrorMessage } from "../lib/monitor-ui";

export interface RefreshLogsOptions {
  autoSelect?: boolean;
  preferredFileName?: string;
}

export function useLogsPanel(route: RoutePage) {
  const [hostLogs, setHostLogs] = useState<LogFileInfo[]>([]);
  const [monitorLogs, setMonitorLogs] = useState<LogFileInfo[]>([]);
  const [selectedLog, setSelectedLog] = useState<LogReadResult | null>(null);
  const [activeLogKind, setActiveLogKind] = useState<LogKind>("monitor");
  const [logsBusy, setLogsBusy] = useState(false);
  const [logsError, setLogsError] = useState<string | null>(null);

  const refreshLogKind = useEffectEvent(async (kind: LogKind, options: RefreshLogsOptions = {}) => {
    setLogsBusy(true);

    try {
      const files = await listLogs(kind);

      startTransition(() => {
        if (kind === "monitor") {
          setMonitorLogs(files);
        } else {
          setHostLogs(files);
        }
      });

      const preferredFileName =
        options.preferredFileName
        ?? (selectedLog?.kind === kind ? selectedLog.fileName : undefined);
      const targetFile = preferredFileName
        ? files.find((item) => item.name === preferredFileName) ?? (options.autoSelect ? files[0] : undefined)
        : options.autoSelect
          ? files[0]
          : undefined;

      if (!targetFile) {
        if (selectedLog?.kind === kind && files.length === 0) {
          startTransition(() => {
            setSelectedLog(null);
          });
        }

        setLogsError(null);
        return;
      }

      const nextLog = await readLog({
        kind,
        fileName: targetFile.name,
      });

      startTransition(() => {
        setSelectedLog(nextLog);
      });
      setLogsError(null);
    } catch (refreshError) {
      setLogsError(toErrorMessage(refreshError));
    } finally {
      setLogsBusy(false);
    }
  });

  const openLog = useEffectEvent(async (kind: LogKind, fileName: string) => {
    setLogsBusy(true);
    setLogsError(null);

    try {
      const result = await readLog({ kind, fileName });
      startTransition(() => {
        setSelectedLog(result);
      });
    } catch (readError) {
      setLogsError(toErrorMessage(readError));
    } finally {
      setLogsBusy(false);
    }
  });

  const selectLogKind = useEffectEvent((kind: LogKind) => {
    startTransition(() => {
      setActiveLogKind(kind);
    });
  });

  useEffect(() => {
    if (route === "logs") {
      void refreshLogKind(activeLogKind, { autoSelect: true });
    }
  }, [activeLogKind, route]);

  return {
    activeLogKind,
    hostLogs,
    logsBusy,
    logsError,
    monitorLogs,
    openLog,
    refreshLogKind,
    selectLogKind,
    selectedLog,
    visibleLogs: activeLogKind === "monitor" ? monitorLogs : hostLogs,
  };
}
