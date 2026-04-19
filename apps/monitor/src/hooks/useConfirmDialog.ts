import { startTransition, useEffect, useEffectEvent, useRef, useState } from "react";

export interface ConfirmDialogRequest {
  message: string;
  title?: string;
  confirmText?: string;
  cancelText?: string;
  variant?: "default" | "danger";
}

export interface ConfirmDialogState {
  title: string;
  message: string;
  confirmText: string;
  cancelText: string;
  variant: "default" | "danger";
}

export function useConfirmDialog() {
  const [dialog, setDialog] = useState<ConfirmDialogState | null>(null);
  const resolverRef = useRef<((result: boolean) => void) | null>(null);

  useEffect(() => () => {
    resolverRef.current?.(false);
    resolverRef.current = null;
  }, []);

  const closeDialog = useEffectEvent((result: boolean) => {
    const resolve = resolverRef.current;
    resolverRef.current = null;

    startTransition(() => {
      setDialog(null);
    });

    resolve?.(result);
  });

  const confirm = useEffectEvent((request: ConfirmDialogRequest): Promise<boolean> => {
    resolverRef.current?.(false);

    return new Promise<boolean>((resolve) => {
      resolverRef.current = resolve;

      startTransition(() => {
        setDialog({
          title: request.title ?? "请注意",
          message: request.message,
          confirmText: request.confirmText ?? "确定",
          cancelText: request.cancelText ?? "取消",
          variant: request.variant ?? "default",
        });
      });
    });
  });

  return {
    closeDialog,
    confirm,
    dialog,
  };
}
