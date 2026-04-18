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
  type DefinitionWorkspaceState,
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

  const [definitionWorkspace, setDefinitionWorkspace] = useState<DefinitionWorkspaceState | null>(null);
  const [definitionError, setDefinitionError] = useState<string | null>(null);

  useEffect(() => {
    startTransition(() => {
      setDefinitionWorkspace(null);
    });
  }, [sessionResetVersion]);

  const closeDefinitionWorkspace = useEffectEvent(() => {
    startTransition(() => {
      setDefinitionWorkspace(null);
    });
  });

  const updateDefinitionField = useEffectEvent((field: keyof DefinitionFormState, value: string | boolean) => {
    startTransition(() => {
      setDefinitionWorkspace((current) => {
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

  const openCreateDefinitionWorkspace = useEffectEvent(async () => {
    setDefinitionError(null);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_create",
      result: "opened",
    });

    startTransition(() => {
      setDefinitionWorkspace({
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

  const openEditDefinitionWorkspace = useEffectEvent(async (appId: string) => {
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
      setDefinitionWorkspace({
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
        setDefinitionWorkspace({
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
          setDefinitionWorkspace({
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
      closeDefinitionWorkspace();
    }
  });

  const openInstanceDefinitionWorkspace = useEffectEvent(async (instance: AppInstance) => {
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
      setDefinitionWorkspace({
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
        setDefinitionWorkspace({
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
          setDefinitionWorkspace({
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
      closeDefinitionWorkspace();
    }
  });

  const handleDefinitionSubmit = useEffectEvent(async (): Promise<boolean> => {
    if (!definitionWorkspace || definitionWorkspace.readOnly) {
      return false;
    }

    const candidateDefinition = definitionFormToModel(definitionWorkspace.form);

    startTransition(() => {
      setDefinitionWorkspace((current) =>
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
          mode: definitionWorkspace.mode,
        },
      });

    try {
      const validation = await runHostAction("validate_definition", (client) =>
        client.validateDefinition(candidateDefinition),
      );

      if (!validation.valid) {
        const issues = mapValidationIssues(validation.errors);

        startTransition(() => {
          setDefinitionWorkspace((current) =>
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
        return false;
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
        setDefinitionWorkspace(null);
      });
      return true;
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
        setDefinitionWorkspace((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: message,
            }
          : current,
        );
      });
      return false;
    }
  });

  const handleDefinitionDelete = useEffectEvent(async (): Promise<boolean> => {
    if (!definitionWorkspace || definitionWorkspace.mode !== "edit" || definitionWorkspace.readOnly) {
      return false;
    }

    if (!window.confirm(`确认删除 App Definition “${definitionWorkspace.form.appId}” 吗？`)) {
      return false;
    }

    startTransition(() => {
      setDefinitionWorkspace((current) =>
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
          appId: definitionWorkspace.form.appId,
        },
      });

    try {
      await runHostAction("delete_definition", (client) =>
        client.deleteDefinition(definitionWorkspace.form.appId),
      );

      onRemoveDefinition(definitionWorkspace.form.appId);
      setDefinitionError(null);

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "delete",
        result: "deleted",
        context: {
          appId: definitionWorkspace.form.appId,
        },
      });

      startTransition(() => {
        setDefinitionWorkspace(null);
      });
      return true;
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
          appId: definitionWorkspace.form.appId,
        },
      });

      startTransition(() => {
        setDefinitionWorkspace((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: message,
            }
          : current,
        );
      });
      return false;
    }
  });

  return {
    closeDefinitionWorkspace,
    definitionWorkspace,
    definitionError,
    handleDefinitionDelete,
    handleDefinitionSubmit,
    openCreateDefinitionWorkspace,
    openEditDefinitionWorkspace,
    openInstanceDefinitionWorkspace,
    updateDefinitionField,
  };
}
