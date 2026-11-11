import {
  Box,
  Card,
  Stack,
  Alert,
  Button,
  Checkbox,
  Divider,
  TextField,
  Typography,
  CardContent,
  FormControlLabel,
  FormGroup,
  CircularProgress,
} from "@mui/material";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { ModuleDestination } from "../types/api";
import { useBackupsApi } from "../api/backupsApi";
import { useModulesApi } from "../api/modulesApi";
import { useEffect, useMemo, useState } from "react";
import { getIgnoredPathsPreset } from "../constants/ignoredPathsPresets";
import type { Module, CreateBackupRequest } from "../types/api";
import { useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../query/queryKeys";
import { BackupModuleSelector } from "../components/backups/BackupModuleSelector";

interface State {
  loading: boolean;
  error: string | null;
  creating: boolean;
  createError: string | null;
}

export default function BackupWizard() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const backupsApi = useBackupsApi();
  const modulesApi = useModulesApi();
  const queryClient = useQueryClient();

  const [state, setState] = useState<State>({
    loading: true,
    error: null,
    creating: false,
    createError: null,
  });
  const [modules, setModules] = useState<Module[]>([]);

  const [sourceId, setSourceId] = useState<string>("");
  const [storageId, setStorageId] = useState<string>("");
  const [tag, setTag] = useState<string>("");
  const [userEditedTag, setUserEditedTag] = useState<boolean>(false);
  const [ignoredPathsInput, setIgnoredPathsInput] = useState<string>("");
  const [disableCompression, setDisableCompression] =
    useState<boolean>(false);
  const [disableEncryption, setDisableEncryption] = useState<boolean>(false);
  const [masterKey, setMasterKey] = useState("");

  useEffect(() => {
    let active = true;
    modulesApi
      .list()
      .then((data) => {
        if (!active) {
          return;
        }
        setModules(data);
        setState((s) => ({ ...s, loading: false }));
      })
      .catch(() => {
        if (!active) {
          return;
        }
        setState((s) => ({
          ...s,
          loading: false,
          error: t("backupWizard.loadFailed"),
        }));
      });
    return () => {
      active = false;
    };
  }, [modulesApi, t]);

  const sources = useMemo(
    () => modules.filter((m) => m.destination === ModuleDestination.Source),
    [modules],
  );
  const storages = useMemo(
    () => modules.filter((m) => m.destination === ModuleDestination.Target),
    [modules],
  );

  const autoTag = useMemo(() => {
    if (sourceId && storageId) {
      const sourceMod = modules.find((m) => m.id === sourceId);
      const storageMod = modules.find((m) => m.id === storageId);
      if (sourceMod && storageMod) {
        return `${sourceMod.tag} ${t("backupWizard.to")} ${storageMod.tag}`;
      }
    }
    return "";
  }, [sourceId, storageId, modules, t]);

  const displayTag = useMemo(() => {
    if (userEditedTag) {
      return tag;
    }
    return autoTag || tag;
  }, [tag, autoTag, userEditedTag]);

  const canCreate = useMemo(
    () => !!sourceId && !!storageId && !!displayTag.trim(),
    [sourceId, storageId, displayTag],
  );
  const ignoredPathsPreset = useMemo(() => {
    const source = modules.find((module) => module.id === sourceId);
    return source ? getIgnoredPathsPreset(source.backupModuleId) : [];
  }, [modules, sourceId]);

  if (state.loading) {
    return (
      <Box display="flex" justifyContent="center" p={4}>
        <CircularProgress />
      </Box>
    );
  }
  if (state.error) {
    return (
      <Box p={2}>
        <Alert severity="error">{state.error}</Alert>
      </Box>
    );
  }

  return (
    <Stack spacing={3}>
      <Typography variant="h5">{t("backupWizard.title")}</Typography>
      {state.createError && <Alert severity="error">{state.createError}</Alert>}
      <Card>
        <CardContent>
          <Stack spacing={3}>
            <Box
              display="flex"
              gap={2}
              alignItems="stretch"
              justifyContent="center"
              sx={{ flexDirection: { xs: "column", md: "row" } }}
            >
              <BackupModuleSelector
                label={t("backupWizard.source")}
                modules={sources}
                value={sourceId}
                onChange={setSourceId}
              />
              <Box
                display="flex"
                alignItems="center"
                justifyContent="center"
                fontSize={48}
                sx={{ minWidth: 48, py: { xs: 2, md: 0 } }}
              >
                →
              </Box>
              <BackupModuleSelector
                label={t("backupWizard.storage")}
                modules={storages}
                value={storageId}
                onChange={setStorageId}
              />
            </Box>
            <Stack spacing={2}>
              <TextField label={t("backupWizard.masterKey")} type="password"
                sx={{ "& .MuiInputLabel-root.Mui-focused": { color: "text.primary" } }}
                autoComplete="new-password" value={masterKey} fullWidth disabled={disableEncryption}
                helperText={t("backupWizard.masterKeyHelp")}
                onChange={(event) => setMasterKey(event.target.value)} />
              <TextField
                label={t("backupWizard.tag")}
                value={displayTag}
                onChange={(e) => {
                  setTag(e.target.value);
                  setUserEditedTag(true);
                }}
                fullWidth
              />
              <Stack spacing={1}>
                <Box display="flex" gap={1} alignItems="center">
                  <TextField
                    label={t("backupWizard.ignoredPaths")}
                    value={ignoredPathsInput}
                    onChange={(e) => setIgnoredPathsInput(e.target.value)}
                    fullWidth
                    multiline
                    minRows={4}
                    placeholder={t("backupWizard.ignoredPathsPlaceholder")}
                  />
                </Box>
                {ignoredPathsPreset.length > 0 && (
                  <Button
                    color="inherit"
                    size="small"
                    variant="outlined"
                    onClick={() =>
                      setIgnoredPathsInput(ignoredPathsPreset.join("\n"))
                    }
                  >
                    {t("backupWizard.applyPreset")}
                  </Button>
                )}
              </Stack>
              <FormGroup row sx={{ gap: 2 }}>
                <FormControlLabel
                  control={
                    <Checkbox
                      checked={disableCompression}
                      onChange={(e) => setDisableCompression(e.target.checked)}
                    />
                  }
                  label={t("backupWizard.disableCompression")}
                />
                <FormControlLabel
                  control={
                    <Checkbox
                      checked={disableEncryption}
                      disabled={Boolean(masterKey.trim())}
                      onChange={(e) => setDisableEncryption(e.target.checked)}
                    />
                  }
                  label={t("backupWizard.disableEncryption")}
                />
              </FormGroup>
            </Stack>
          </Stack>
        </CardContent>
      </Card>
      <Divider />
      <Stack direction="row" spacing={2}>
        <Button color="inherit" variant="outlined" onClick={() => navigate(-1)}>
          {t("common.back")}
        </Button>
        <Button
          variant="contained"
          disabled={!canCreate || state.creating}
          onClick={async () => {
            try {
              setState((s) => ({ ...s, creating: true, createError: null }));
              const payload: CreateBackupRequest = {
                sourceId,
                storageId,
                tag: displayTag.trim(),
                ignoredPaths: ignoredPathsInput
                  .split(/\r?\n/)
                  .map((value) => value.trim())
                  .filter((value) => value !== ""),
                disableCompression,
                disableEncryption,
                masterKey: masterKey.trim() || undefined,
              };
              await backupsApi.create(payload);
              await queryClient.invalidateQueries({
                queryKey: queryKeys.backups,
                refetchType: "all",
              });
              navigate("/backups");
            } catch (caughtError) {
              const message =
                caughtError instanceof Error
                  ? caughtError.message
                  : t("backupWizard.createError");
              setState((s) => ({
                ...s,
                creating: false,
                createError: message || t("backupWizard.createError"),
              }));
            }
          }}
        >
          {state.creating
            ? t("wizard.creating")
            : t("backupWizard.createBackup")}
        </Button>
      </Stack>
    </Stack>
  );
}
