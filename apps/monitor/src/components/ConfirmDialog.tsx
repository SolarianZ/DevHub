import { useEffect } from "react";
import type { ConfirmDialogState } from "../hooks/useConfirmDialog";

interface ConfirmDialogProps {
  dialog: ConfirmDialogState | null;
  onClose: (confirmed: boolean) => void;
}

export function ConfirmDialog(props: ConfirmDialogProps) {
  const { dialog, onClose } = props;

  useEffect(() => {
    if (!dialog) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose(false);
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [dialog, onClose]);

  if (!dialog) {
    return null;
  }

  return (
    <div
      className="confirm-dialog-backdrop"
      role="presentation"
      onClick={() => onClose(false)}
    >
      <div
        className="confirm-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        aria-describedby="confirm-dialog-message"
        onClick={(event) => event.stopPropagation()}
      >
        <h2 id="confirm-dialog-title" className="confirm-dialog-title">{dialog.title}</h2>
        <p id="confirm-dialog-message" className="confirm-dialog-message">{dialog.message}</p>

        <div className="confirm-dialog-actions">
          <button
            type="button"
            className={dialog.variant === "danger" ? "button-danger" : undefined}
            onClick={() => onClose(true)}
          >
            {dialog.confirmText}
          </button>
          <button
            type="button"
            className="button-secondary"
            autoFocus
            onClick={() => onClose(false)}
          >
            {dialog.cancelText}
          </button>
        </div>
      </div>
    </div>
  );
}
