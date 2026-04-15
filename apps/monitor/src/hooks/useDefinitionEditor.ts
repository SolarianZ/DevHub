import { DevHubRpcError, DevHubRpcErrorCode, type AppDefinition, type AppInstance, type DevHubClient } from "@devhub/sdk";
import { startTransition, useEffect, useEffectEvent, useState } from "react";
import {
  createEmptyDefinitionForm,
  definitionFormToModel,
  definitionToForm,
  mapValidationIssues,
  type DefinitionFormState,
} from "../lib/definition-form";
import type { FrontendLogInput } from "../lib/models";
import {
  type DefinitionDialogState,
  createMissingDefinitionForm,
  toErrorMessage,
} from "../lib/monitor-ui";

interface DefinitionEditorOptions {
  onRemoveDefinition: (appId: string) => void;
  onReplaceDefinition: (definition: AppDefinition) => void;
  recordFrontendLog: (entry: FrontendLogInput) => void;
  runHostAction: <T>(action: string, execute: (client: DevHubClient) => Promise<T>) => Promise<T>;
  sessionResetVersion: number;
}

export function useDefinitionEditor(options: DefinitionEditorOptions) {
  const { onRemoveDefinition, onReplaceDefinition, recordFrontendLog, runHostAction, sessionResetVersion } = options;

  const [definitionDialog, setDefinitionDialog] = useState<DefinitionDialogState | null>(null);
  const [definitionError, setDefinitionError] = useState<string | null>(null);

  useEffect(() => {
    startTransition(() => {
      setDefinitionDialog(null);
    });
  }, [sessionResetVersion]);

  const closeDefinitionDialog = useEffectEvent(() => {
    startTransition(() => {
      setDefinitionDialog(null);
    });
  });

  const updateDefinitionField = useEffectEvent((field: keyof DefinitionFormState, value: string | boolean) => {
    startTransition(() => {
      setDefinitionDialog((current) => {
        if (!current) {
          return current;
        }

        const nextForm = {
          ...current.form,
          [field]: value,
        };

        if (field === "enableLaunch" && value === false) {
          nextForm.launchExePath = "";
          nextForm.launchArgsTemplate = "";
          nextForm.launchWorkingDirectory = "";
          nextForm.launchDedupeKeyTemplate = "";
        }

        return {
          ...current,
          form: nextForm,
          fieldErrors: {},
          submitError: null,
        };
      });
    });
  });

  const openCreateDefinitionDialog = useEffectEvent(async () => {
    setDefinitionError(null);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_create",
      result: "opened",
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "create",
        title: "新增 App Definition",
        subtitle: "提交前会先通过 hub.apps.validateDefinition 进行预校验。",
        form: createEmptyDefinitionForm(),
        fieldErrors: {},
        loading: false,
        saving: false,
        readOnly: false,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });
  });

  const openEditDefinitionDialog = useEffectEvent(async (appId: string) => {
    setDefinitionError(null);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_edit",
      result: "requested",
      context: {
        appId,
      },
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "edit",
        title: "编辑 App Definition",
        subtitle: "编辑模式固定读取最新持久化定义，再允许保存或删除。",
        form: createMissingDefinitionForm(appId),
        fieldErrors: {},
        loading: true,
        saving: false,
        readOnly: false,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });

    try {
      const definition = await runHostAction("open_edit_definition", (client) =>
        client.getDefinition(appId),
      );

      startTransition(() => {
        setDefinitionDialog({
          mode: "edit",
          title: "编辑 App Definition",
          subtitle: "编辑模式固定读取最新持久化定义，再允许保存或删除。",
          form: definitionToForm(definition),
          fieldErrors: {},
          loading: false,
          saving: false,
          readOnly: false,
          missing: false,
          emptyStateMessage: null,
          submitError: null,
        });
      });
    } catch (dialogError) {
      if (dialogError instanceof DevHubRpcError && dialogError.is(DevHubRpcErrorCode.AppDefinitionNotFound)) {
        startTransition(() => {
          setDefinitionDialog({
            mode: "edit",
            title: "定义已不可用",
            subtitle: "该定义在打开编辑器前已被删除，当前窗口仅展示只读空状态。",
            form: createMissingDefinitionForm(appId),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `定义 ${appId} 已不存在或已被删除。`,
            submitError: null,
          });
        });
        return;
      }

      setDefinitionError(toErrorMessage(dialogError));
      closeDefinitionDialog();
    }
  });

  const openInstanceDefinitionDialog = useEffectEvent(async (instance: AppInstance) => {
    setDefinitionError(null);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_view",
      result: "requested",
      context: {
        appId: instance.appId,
        instanceId: instance.instanceId,
      },
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "view",
        title: "实例关联定义",
        subtitle: `实例 ${instance.instanceId} 的定义详情只读展示。`,
        form: createMissingDefinitionForm(instance.appId),
        fieldErrors: {},
        loading: true,
        saving: false,
        readOnly: true,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });

    try {
      const definition = await runHostAction("open_view_definition", (client) =>
        client.getDefinition(instance.appId),
      );

      startTransition(() => {
        setDefinitionDialog({
          mode: "view",
          title: "实例关联定义",
          subtitle: `实例 ${instance.instanceId} 的定义详情只读展示。`,
          form: definitionToForm(definition),
          fieldErrors: {},
          loading: false,
          saving: false,
          readOnly: true,
          missing: false,
          emptyStateMessage: null,
          submitError: null,
        });
      });
    } catch (dialogError) {
      if (dialogError instanceof DevHubRpcError && dialogError.is(DevHubRpcErrorCode.AppDefinitionNotFound)) {
        startTransition(() => {
          setDefinitionDialog({
            mode: "view",
            title: "定义不存在",
            subtitle: `实例 ${instance.instanceId} 仍然保留注册记录，但对应定义已不可用。`,
            form: createMissingDefinitionForm(instance.appId),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `实例 ${instance.instanceId} 对应的定义 ${instance.appId} 不存在或已被删除。`,
            submitError: null,
          });
        });
        return;
      }

      setDefinitionError(toErrorMessage(dialogError));
      closeDefinitionDialog();
    }
  });

  const handleDefinitionSubmit = useEffectEvent(async () => {
    if (!definitionDialog || definitionDialog.readOnly) {
      return;
    }

    const candidateDefinition = definitionFormToModel(definitionDialog.form);

    startTransition(() => {
      setDefinitionDialog((current) =>
        current
          ? {
              ...current,
              saving: true,
              fieldErrors: {},
              submitError: null,
            }
          : current,
      );
    });

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "submit",
      result: "validating",
      context: {
        appId: candidateDefinition.appId,
        mode: definitionDialog.mode,
      },
    });

    try {
      const validation = await runHostAction("validate_definition", (client) =>
        client.validateDefinition(candidateDefinition),
      );

      if (!validation.valid) {
        const issues = mapValidationIssues(validation.errors);

        startTransition(() => {
          setDefinitionDialog((current) =>
            current
              ? {
                  ...current,
                  saving: false,
                  fieldErrors: issues,
                  submitError: "预校验未通过，请修正下列字段错误后再提交。",
                }
              : current,
          );
        });
        return;
      }

      const savedDefinition = await runHostAction("upsert_definition", (client) =>
        client.upsertDefinition(candidateDefinition),
      );

      onReplaceDefinition(savedDefinition);
      setDefinitionError(null);

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "submit",
        result: "saved",
        context: {
          appId: savedDefinition.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog(null);
      });
    } catch (submitError) {
      const message = toErrorMessage(submitError);
      setDefinitionError(message);

      recordFrontendLog({
        level: "error",
        category: "frontend.definition",
        action: "submit",
        result: "failed",
        message,
        context: {
          appId: candidateDefinition.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: message,
              }
            : current,
        );
      });
    }
  });

  const handleDefinitionDelete = useEffectEvent(async () => {
    if (!definitionDialog || definitionDialog.mode !== "edit" || definitionDialog.readOnly) {
      return;
    }

    if (!window.confirm(`确认删除 App Definition “${definitionDialog.form.appId}” 吗？`)) {
      return;
    }

    startTransition(() => {
      setDefinitionDialog((current) =>
        current
          ? {
              ...current,
              saving: true,
              submitError: null,
            }
          : current,
      );
    });

    recordFrontendLog({
      level: "warn",
      category: "frontend.definition",
      action: "delete",
      result: "requested",
      context: {
        appId: definitionDialog.form.appId,
      },
    });

    try {
      await runHostAction("delete_definition", (client) =>
        client.deleteDefinition(definitionDialog.form.appId),
      );

      onRemoveDefinition(definitionDialog.form.appId);
      setDefinitionError(null);

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "delete",
        result: "deleted",
        context: {
          appId: definitionDialog.form.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog(null);
      });
    } catch (deleteError) {
      const message = toErrorMessage(deleteError);
      setDefinitionError(message);

      recordFrontendLog({
        level: "error",
        category: "frontend.definition",
        action: "delete",
        result: "failed",
        message,
        context: {
          appId: definitionDialog.form.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: message,
              }
            : current,
        );
      });
    }
  });

  return {
    closeDefinitionDialog,
    definitionDialog,
    definitionError,
    handleDefinitionDelete,
    handleDefinitionSubmit,
    openCreateDefinitionDialog,
    openEditDefinitionDialog,
    openInstanceDefinitionDialog,
    updateDefinitionField,
  };
}
