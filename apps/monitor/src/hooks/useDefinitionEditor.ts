import {
  DevHubRpcError,
  DevHubRpcErrorCode,
  type AppDefinition,
  type AppDefinitionIdentity,
  type AppInstance,
  type DevHubClient,
} from "@devhub/sdk";
import { startTransition, useEffect, useEffectEvent, useState } from "react";
import {
  areDefinitionFormsEqual,
  createEmptyDefinitionForm,
  definitionFormToModel,
  definitionToForm,
  mapValidationIssues,
  type DefinitionFormState,
} from "../lib/definition-form";
import type { FrontendLogInput } from "../lib/models";
import {
  createDefinitionIdentity,
  formatDefinitionIdentity,
  type DefinitionWorkspaceState,
  createMissingDefinitionForm,
  toErrorMessage,
} from "../lib/monitor-ui";
import type { ConfirmDialogRequest } from "./useConfirmDialog";

interface DefinitionEditorOptions {
  confirmAction: (request: ConfirmDialogRequest) => Promise<boolean>;
  onRemoveDefinition: (identity: AppDefinitionIdentity) => void;
  onReplaceDefinition: (definition: AppDefinition) => void;
  recordFrontendLog: (entry: FrontendLogInput) => void;
  runHostAction: <T>(action: string, execute: (client: DevHubClient) => Promise<T>) => Promise<T>;
  sessionResetVersion: number;
}

export function useDefinitionEditor(options: DefinitionEditorOptions) {
  const { confirmAction, onRemoveDefinition, onReplaceDefinition, recordFrontendLog, runHostAction, sessionResetVersion } = options;

  const [definitionWorkspace, setDefinitionWorkspace] = useState<DefinitionWorkspaceState | null>(null);
  const [definitionBaseline, setDefinitionBaseline] = useState<DefinitionFormState | null>(null);
  const [definitionError, setDefinitionError] = useState<string | null>(null);
  const definitionDirty = definitionWorkspace !== null
    && !definitionWorkspace.readOnly
    && !definitionWorkspace.loading
    && !definitionWorkspace.missing
    && definitionBaseline !== null
    && !areDefinitionFormsEqual(definitionWorkspace.form, definitionBaseline);

  useEffect(() => {
    startTransition(() => {
      setDefinitionWorkspace(null);
      setDefinitionBaseline(null);
    });
  }, [sessionResetVersion]);

  const closeDefinitionWorkspace = useEffectEvent(() => {
    startTransition(() => {
      setDefinitionWorkspace(null);
      setDefinitionBaseline(null);
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
    const initialForm = createEmptyDefinitionForm();

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_create",
      result: "opened",
    });

    startTransition(() => {
      setDefinitionWorkspace({
        mode: "create",
        title: "新增 App 定义",
        form: initialForm,
        fieldErrors: {},
        loading: false,
        saving: false,
        readOnly: false,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
      setDefinitionBaseline(initialForm);
    });
  });

  const openEditDefinitionWorkspace = useEffectEvent(async (identity: AppDefinitionIdentity) => {
    setDefinitionError(null);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_edit",
      result: "requested",
      context: {
        appId: identity.appId,
        scope: identity.scope,
      },
    });

    startTransition(() => {
      setDefinitionBaseline(null);
      setDefinitionWorkspace({
        mode: "edit",
        title: "编辑 App Definition",
        form: createMissingDefinitionForm(identity),
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
        client.getDefinition(identity),
      );
      const nextForm = definitionToForm(definition);

      startTransition(() => {
        setDefinitionWorkspace({
          mode: "edit",
          title: "编辑 App Definition",
          form: nextForm,
          fieldErrors: {},
          loading: false,
          saving: false,
          readOnly: false,
          missing: false,
          emptyStateMessage: null,
          submitError: null,
        });
        setDefinitionBaseline(nextForm);
      });
    } catch (dialogError) {
      if (dialogError instanceof DevHubRpcError && dialogError.is(DevHubRpcErrorCode.AppDefinitionNotFound)) {
        startTransition(() => {
          setDefinitionBaseline(null);
          setDefinitionWorkspace({
            mode: "edit",
            title: "定义已不可用",
            form: createMissingDefinitionForm(identity),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `定义 ${formatDefinitionIdentity(identity)} 已不存在或已被删除。`,
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
    const identity = createDefinitionIdentity(instance.appId, instance.scope);

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_view",
      result: "requested",
      context: {
        appId: instance.appId,
        instanceId: instance.instanceId,
        scope: identity.scope,
      },
    });

    startTransition(() => {
      setDefinitionBaseline(null);
      setDefinitionWorkspace({
        mode: "view",
        title: "实例关联定义",
        form: createMissingDefinitionForm(identity),
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
        client.getDefinition(identity),
      );
      const nextForm = definitionToForm(definition);

      startTransition(() => {
        setDefinitionWorkspace({
          mode: "view",
          title: "实例关联定义",
          form: nextForm,
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
          setDefinitionBaseline(null);
          setDefinitionWorkspace({
            mode: "view",
            title: "定义不存在",
            form: createMissingDefinitionForm(identity),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `实例 ${instance.instanceId} 对应的定义 ${formatDefinitionIdentity(identity)} 不存在或已被删除。`,
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
        scope: candidateDefinition.scope,
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
          scope: savedDefinition.scope,
        },
      });

      startTransition(() => {
        setDefinitionWorkspace(null);
        setDefinitionBaseline(null);
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
          scope: candidateDefinition.scope,
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

    const identity = createDefinitionIdentity(definitionWorkspace.form.appId, definitionWorkspace.form.scope);
    const confirmed = await confirmAction({
      message: `确认删除 App Definition “${formatDefinitionIdentity(identity)}” 吗？`,
      variant: "danger",
    });

    if (!confirmed) {
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
        appId: identity.appId,
        scope: identity.scope,
      },
    });

    try {
      await runHostAction("delete_definition", (client) =>
        client.deleteDefinition(identity),
      );

      onRemoveDefinition(identity);
      setDefinitionError(null);

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "delete",
        result: "deleted",
        context: {
          appId: identity.appId,
          scope: identity.scope,
        },
      });

      startTransition(() => {
        setDefinitionWorkspace(null);
        setDefinitionBaseline(null);
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
          appId: identity.appId,
          scope: identity.scope,
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
    definitionDirty,
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
