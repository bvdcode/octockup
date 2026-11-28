import {
  Box,
  Alert,
  Stack,
  Button,
  Typography,
  CircularProgress,
} from "@mui/material";
import type { ClipboardEvent } from "react";
import { useEffect, useMemo, useState, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { TestActions } from "./wizard/TestActions";
import { ModuleHeader } from "./wizard/ModuleHeader";
import { useWizardForm } from "../hooks/useWizardForm";
import { useModuleTest } from "../hooks/useModuleTest";
import { ParametersForm } from "./wizard/ParametersForm";
import { DirectoryBrowser } from "./wizard/DirectoryBrowser";
import { ArrowBack } from "@mui/icons-material";
import { useSnackbar } from "notistack";
import { useModuleMetadata } from "../hooks/useModuleMetadata";
import { useNavigate, useSearchParams } from "react-router-dom";
import { ModuleDestination } from "../types/api";
import type { ModuleProviderInfo } from "../types/api";
import { useDirectoryBrowser } from "../hooks/useDirectoryBrowser";
import { CHECKBOX_PARAMETERS } from "../constants/checkboxParameters";
import {
  isEncryptedPuttyKey,
  PuttyKeyError,
  SFTP_PROVIDER_ID,
  unlockPuttyKey,
} from "../utils/puttyPrivateKey";
import { useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../query/queryKeys";

type ModuleType = "source" | "storage" | "target";

interface BackupModuleWizardProps {
  moduleType: ModuleType;
  apiClient: {
    listProviders: () => Promise<ModuleProviderInfo[]>;
    listProvidersByType: (
      type: "source" | "storage",
    ) => Promise<ModuleProviderInfo[]>;
    create: (
      providerId: string,
      destination: ModuleDestination,
      tag: string,
      backupModuleId: string,
      parameters: Record<string, string>,
    ) => Promise<void>;
    test: (
      providerId: string,
      parameters: Record<string, string>,
      destination: ModuleDestination,
    ) => Promise<void>;
    getDirectories: (
      providerId: string,
      parameters: Record<string, string>,
    ) => Promise<string[]>;
    list: () => Promise<Array<{ id: string; tag: string }>>; // modules list
  };
  backRoute: string;
}

export default function BackupModuleWizard({
  moduleType,
  apiClient,
  backRoute,
}: BackupModuleWizardProps) {
  const { t } = useTranslation();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();
  const providerId = searchParams.get("type") || ""; // provider full name

  // Memoize provider fetch to avoid recreating function every render (prevents infinite fetch loop)
  const fetchProviders = useCallback(
    () =>
      apiClient.listProvidersByType(
        moduleType === "source" ? "source" : "storage",
      ),
    [apiClient, moduleType],
  );

  const { loading, error, moduleMeta } = useModuleMetadata(
    providerId,
    fetchProviders,
  );
  const {
    params,
    tag,
    hasUnsavedChanges,
    initializeParams,
    updateParam,
    updateTag,
    resetUnsavedChanges,
    bulkUpdateParams,
  } = useWizardForm(moduleMeta);
  const [privateKeyPassphrase, setPrivateKeyPassphrase] = useState("");

  const prepareSftpParameters = useCallback(
    async (
      parameters: Record<string, string>,
    ): Promise<Record<string, string>> => {
      if (
        providerId !== SFTP_PROVIDER_ID ||
        !isEncryptedPuttyKey(parameters.password ?? "")
      ) {
        return parameters;
      }

      try {
        const password = await unlockPuttyKey(
          parameters.password,
          privateKeyPassphrase,
        );
        updateParam("password", password);
        setPrivateKeyPassphrase("");
        return { ...parameters, password };
      } catch (error) {
        const key =
          error instanceof PuttyKeyError ? error.code : "sftpKeyInvalid";
        throw new Error(t(`wizard.${key}`), { cause: error });
      }
    },
    [privateKeyPassphrase, providerId, t, updateParam],
  );

  const preparedApiClient = useMemo(
    () => ({
      test: async (
        id: string,
        parameters: Record<string, string>,
        requestedDestination: ModuleDestination,
      ) =>
        apiClient.test(
          id,
          await prepareSftpParameters(parameters),
          requestedDestination,
        ),
      getDirectories: async (
        id: string,
        parameters: Record<string, string>,
      ) =>
        apiClient.getDirectories(
          id,
          await prepareSftpParameters(parameters),
        ),
    }),
    [apiClient, prepareSftpParameters],
  );

  const browser = useDirectoryBrowser(
    moduleMeta,
    params,
    providerId,
    preparedApiClient,
  );
  const destination =
    moduleType === "source"
      ? ModuleDestination.Source
      : ModuleDestination.Target;
  const test = useModuleTest(
    moduleMeta,
    params,
    providerId,
    destination,
    preparedApiClient,
  );

  const [creating, setCreating] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [existingTags, setExistingTags] = useState<string[]>([]);

  // Initialize params when metadata loads
  useEffect(() => {
    initializeParams();
  }, [initializeParams]);

  // Load existing tags
  useEffect(() => {
    apiClient
      .list()
      .then((items) => {
        setExistingTags(items.map((item) => item.tag.toLowerCase()));
      })
      .catch(() => {
        // ignore errors
      });
  }, [apiClient]);

  // Reset test state when params change (except path and checkboxes)
  const handleParamChange = (name: string, value: string) => {
    updateParam(name, value);

    if (providerId === SFTP_PROVIDER_ID && name === "password") {
      setPrivateKeyPassphrase("");
    }
    
    // Don't reset test for checkbox parameters (they don't affect connection)
    if (!CHECKBOX_PARAMETERS.includes(name)) {
      test.resetTest();
    }
    
    if (name !== "path" && moduleMeta?.requiredParameters.includes("path")) {
      browser.resetBrowser();
    }
  };

  const handlePrivateKeyPassphraseChange = (value: string) => {
    setPrivateKeyPassphrase(value);
    test.resetTest();
    browser.resetBrowser();
  };

  // Handle multi-line paste
  const handleParamsPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    if (!moduleMeta) return;
    const text = e.clipboardData?.getData("text") ?? "";
    if (!text) return;
    const rawLines = text.split(/\r?\n/);
    if (rawLines.length === 0) return;
    if (rawLines[rawLines.length - 1] === "") rawLines.pop();
    const lines = rawLines;
    
    // Ignore smart paste for single line - let browser handle it normally
    if (lines.length === 1) return;
    
    const keys = moduleMeta.requiredParameters || [];
    
    // Allow paste if lines count is less than or equal to params count
    // Skip checkbox parameters when matching
    const nonCheckboxKeys = keys.filter(k => !CHECKBOX_PARAMETERS.includes(k));
    
    // Only proceed if we have multiple lines and not more than available fields
    if (lines.length === 0 || lines.length > nonCheckboxKeys.length) return;

    e.preventDefault();
    const next: Record<string, string> = { ...params };
    
    // Fill only the number of lines we have, matching to non-checkbox parameters
    nonCheckboxKeys.forEach((k, i) => {
      if (i < lines.length) {
        next[k] = lines[i];
      }
    });
    
    bulkUpdateParams(next);
    test.resetTest();

    if (keys.includes("path")) {
      const pastedPath = next["path"] || "";
      browser.loadDirectories(pastedPath);
    }
  };

  // Handle directory navigation with path sync
  const handleNavigateToDir = (dir: string) => {
    browser.navigateToDir(dir);
    if (moduleMeta?.requiredParameters.includes("path")) {
      const sep = moduleMeta.pathSeparator || "/";
      const newPath =
        browser.browserPath === sep
          ? `${browser.browserPath}${dir}`
          : `${browser.browserPath}${sep}${dir}`;
      updateParam("path", newPath);
    }
  };

  const handleNavigateUp = () => {
    if (!moduleMeta) return;
    const sep = moduleMeta.pathSeparator || "/";
    const lastSepIndex = browser.browserPath.lastIndexOf(sep);
    const newPath =
      lastSepIndex <= 0 ? sep : browser.browserPath.substring(0, lastSepIndex);
    browser.navigateUp();
    if (moduleMeta.requiredParameters.includes("path")) {
      updateParam("path", newPath);
    }
  };

  const handleNavigateToRoot = () => {
    if (!moduleMeta) return;
    const sep = moduleMeta.pathSeparator || "/";
    browser.navigateToRoot();
    if (moduleMeta.requiredParameters.includes("path")) {
      updateParam("path", sep);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!moduleMeta || !tag.trim()) {
      setSubmitError(t("wizard.fillParameters"));
      return;
    }
    if (existingTags.includes(tag.trim().toLowerCase())) {
      setSubmitError(t("wizard.tagAlreadyExists"));
      return;
    }
    const invalidHttp = test.validateHttpEndpoint();
    if (invalidHttp) {
      setSubmitError(invalidHttp);
      return;
    }
    try {
      setCreating(true);
      setSubmitError(null);
      const preparedParameters = await prepareSftpParameters(params);
      await apiClient.create(
        providerId,
        moduleType === "source"
          ? ModuleDestination.Source
          : ModuleDestination.Target,
        tag.trim(),
        providerId,
        preparedParameters,
      );
      await queryClient.invalidateQueries({
        queryKey: queryKeys.modules,
        refetchType: "all",
      });
      resetUnsavedChanges();
      enqueueSnackbar(
        t(moduleType === "source" ? "wizard.sourceCreatedSuccess" : "wizard.storageCreatedSuccess"),
        { variant: "success" },
      );
      navigate(backRoute);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setSubmitError(msg || t("wizard.createError"));
    } finally {
      setCreating(false);
    }
  };

  const handleBack = () => {
    if (hasUnsavedChanges) {
      const confirmed = window.confirm(t("wizard.unsavedChanges"));
      if (!confirmed) return;
    }
    navigate(backRoute);
  };

  const hasDirectoryBrowser = moduleMeta?.requiredParameters.includes("path");

  if (loading) {
    return (
      <Box display="flex" justifyContent="center" p={4}>
        <CircularProgress />
      </Box>
    );
  }

  if (error) {
    return (
      <Box p={2}>
        <Alert severity="error">{error}</Alert>
        <Button
          sx={{ mt: 2 }}
          variant="outlined"
          color="inherit"
          startIcon={<ArrowBack />}
          onClick={() => navigate(backRoute)}
        >
          {t("common.back")}
        </Button>
      </Box>
    );
  }

  return (
    <Stack
      spacing={3}
      flex={hasDirectoryBrowser ? 1 : undefined}
      sx={(theme) => hasDirectoryBrowser ? { [theme.breakpoints.down(960)]: { pb: 8 } } : {}}
    >
      <Stack direction="row" spacing={2} alignItems="center">
        <Button
          variant="outlined"
          color="inherit"
          startIcon={<ArrowBack />}
          onClick={handleBack}
          disabled={creating}
        >
          {t("common.back")}
        </Button>
        <Typography variant="h5">
          {t(
            moduleType === "source"
              ? "sources.newSource"
              : "storages.newStorage",
          )}
        </Typography>
      </Stack>
      {moduleMeta && (
        <Stack
          component="form"
          spacing={3}
          flex={hasDirectoryBrowser ? 1 : undefined}
          onSubmit={handleSubmit}
        >
          <ModuleHeader moduleMeta={moduleMeta} />

          <ParametersForm
            key={moduleMeta.id}
            moduleMeta={moduleMeta}
            params={params}
            privateKeyPassphrase={privateKeyPassphrase}
            tag={tag}
            onParamChange={handleParamChange}
            onPrivateKeyPassphraseChange={
              handlePrivateKeyPassphraseChange
            }
            onTagChange={updateTag}
            onParamsPaste={handleParamsPaste}
            disabled={creating}
          />

          {hasDirectoryBrowser && (
            <DirectoryBrowser
              browserPath={browser.browserPath}
              browserDirs={browser.browserDirs}
              browserLoading={browser.browserLoading}
              onNavigateToRoot={handleNavigateToRoot}
              onNavigateUp={handleNavigateUp}
              onNavigateToDir={handleNavigateToDir}
              disabled={creating}
            />
          )}

          {submitError && <Alert severity="error">{submitError}</Alert>}

          <TestActions
            testLoading={test.testLoading}
            testMessage={test.testMessage}
            testError={test.testError}
            creating={creating}
            moduleType={moduleType}
            onTest={test.runTest}
          />
        </Stack>
      )}
    </Stack>
  );
}
