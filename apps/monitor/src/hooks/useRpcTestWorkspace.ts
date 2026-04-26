import { startTransition, useEffect, useEffectEvent, useRef, useState } from "react";
import type { MonitorRuntimeConnectionInfo } from "../lib/models";
import {
  getRpcTestStatusLabel,
  type RpcTestRequestStatus,
  type RpcTestValidationFeedback,
  type RpcTestWorkspaceViewModel,
} from "../lib/monitor-ui";
import { sendRawRpcRequest } from "../lib/raw-rpc";
import { getRpcTestDraftPlaceholder, validateRpcTestDraft } from "../lib/rpc-test";
import { toErrorMessage } from "../lib/monitor-ui";

interface UseRpcTestWorkspaceOptions {
  connection?: MonitorRuntimeConnectionInfo | null;
  sessionResetVersion: number;
}

const IDLE_STATUS_DETAIL = "尚未发送请求。";
const CONNECTION_UNAVAILABLE_DETAIL = "当前没有可用 Host 连接，发送操作已禁用。";

export function useRpcTestWorkspace(options: UseRpcTestWorkspaceOptions) {
  const { connection, sessionResetVersion } = options;

  const [draft, setDraft] = useState("");
  const [validationFeedback, setValidationFeedback] = useState<RpcTestValidationFeedback | null>(null);
  const [requestStatus, setRequestStatus] = useState<RpcTestRequestStatus>("idle");
  const [requestStatusDetail, setRequestStatusDetail] = useState(IDLE_STATUS_DETAIL);
  const [requestError, setRequestError] = useState<string | null>(null);
  const [resultText, setResultText] = useState<string | null>(null);
  const [inFlightController, setInFlightController] = useState<AbortController | null>(null);

  const attemptRef = useRef(0);
  const inFlightControllerRef = useRef<AbortController | null>(null);
  const connectionKey = connection
    ? `${connection.rpcEndpoint}|${connection.token}|${connection.runtime.protocolVersion}`
    : "unavailable";

  useEffect(() => {
    inFlightControllerRef.current = inFlightController;
  }, [inFlightController]);

  const abortCurrentAttempt = useEffectEvent((nextStatus: RpcTestRequestStatus, nextStatusDetail: string) => {
    attemptRef.current += 1;
    inFlightControllerRef.current?.abort();
    inFlightControllerRef.current = null;

    startTransition(() => {
      setInFlightController(null);
      setRequestStatus(nextStatus);
      setRequestStatusDetail(nextStatusDetail);
    });
  });

  useEffect(() => {
    abortCurrentAttempt("idle", connection ? IDLE_STATUS_DETAIL : CONNECTION_UNAVAILABLE_DETAIL);
    startTransition(() => {
      setRequestError(null);
      setResultText(null);
    });
  }, [connectionKey, sessionResetVersion]);

  useEffect(() => {
    return () => {
      inFlightControllerRef.current?.abort();
      inFlightControllerRef.current = null;
      attemptRef.current += 1;
    };
  }, []);

  const updateDraft = useEffectEvent((value: string) => {
    setDraft(value);
    setValidationFeedback(null);
    if (requestStatus === "validation_failed") {
      setRequestStatus("idle");
      setRequestStatusDetail(connection ? IDLE_STATUS_DETAIL : CONNECTION_UNAVAILABLE_DETAIL);
    }
    setRequestError(null);
  });

  const validateDraft = useEffectEvent(() => {
    const validation = validateRpcTestDraft(draft);
    if (!validation.ok) {
      startTransition(() => {
        setValidationFeedback({
          kind: "error",
          message: validation.error,
        });
        setRequestStatus("validation_failed");
        setRequestStatusDetail("当前请求文本未通过校验，未发送到 Host。");
        setRequestError(null);
      });
      return null;
    }

    startTransition(() => {
      setValidationFeedback({
        kind: "success",
        message: validation.message,
      });
      if (requestStatus === "validation_failed") {
        setRequestStatus("idle");
        setRequestStatusDetail(connection ? IDLE_STATUS_DETAIL : CONNECTION_UNAVAILABLE_DETAIL);
      }
      setRequestError(null);
    });

    return validation.envelope;
  });

  const sendDraft = useEffectEvent(async () => {
    if (!connection || inFlightControllerRef.current) {
      return;
    }

    const envelope = validateDraft();
    if (!envelope) {
      return;
    }

    const controller = new AbortController();
    const attemptId = attemptRef.current + 1;
    attemptRef.current = attemptId;
    inFlightControllerRef.current = controller;

    startTransition(() => {
      setInFlightController(controller);
      setValidationFeedback(null);
      setRequestStatus("waiting");
      setRequestStatusDetail(`等待 Host 回复：${envelope.method}`);
      setRequestError(null);
      setResultText(null);
    });

    try {
      const responseText = await sendRawRpcRequest(connection, envelope, {
        signal: controller.signal,
      });

      if (attemptRef.current !== attemptId) {
        return;
      }

      inFlightControllerRef.current = null;
      startTransition(() => {
        setInFlightController(null);
        setRequestStatus("received");
        setRequestStatusDetail(`已收到 Host 对 ${envelope.method} 的回复。`);
        setRequestError(null);
        setResultText(responseText);
      });
    } catch (error) {
      if (attemptRef.current !== attemptId) {
        return;
      }

      if (controller.signal.aborted) {
        return;
      }

      inFlightControllerRef.current = null;
      startTransition(() => {
        setInFlightController(null);
        setRequestStatus("request_failed");
        setRequestStatusDetail(`请求 ${envelope.method} 时发生传输错误。`);
        setRequestError(toErrorMessage(error));
      });
    }
  });

  const cancelRequest = useEffectEvent(() => {
    if (!inFlightControllerRef.current) {
      return;
    }

    abortCurrentAttempt("cancelled", "已取消等待当前请求，后续迟到回复将被丢弃。");
    startTransition(() => {
      setRequestError(null);
    });
  });

  const workspace: RpcTestWorkspaceViewModel = {
    available: Boolean(connection),
    rpcEndpoint: connection?.rpcEndpoint ?? null,
    draft,
    draftPlaceholder: getRpcTestDraftPlaceholder(),
    validationFeedback,
    requestStatus,
    requestStatusLabel: getRpcTestStatusLabel(requestStatus),
    requestStatusDetail,
    requestError,
    resultText,
    canValidate: !inFlightController,
    canSend: Boolean(connection) && !inFlightController,
    canCancel: Boolean(inFlightController),
  };

  return {
    workspace,
    updateDraft,
    validateDraft,
    sendDraft,
    cancelRequest,
  };
}
