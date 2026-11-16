import {
  Alert,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  Typography,
} from "@mui/material";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useModulesApi } from "../../api/modulesApi";
import { queryKeys } from "../../query/queryKeys";
import type { Module, ModuleParameters, ModuleProviderInfo } from "../../types/api";
import { ModuleDestination } from "../../types/api";
import { getApiErrorMessage } from "../../utils/apiError";
import {
  isEncryptedPuttyKey,
  PuttyKeyError,
  SFTP_PROVIDER_ID,
  unlockPuttyKey,
} from "../../utils/puttyPrivateKey";
import { ParametersForm } from "../wizard/ParametersForm";

interface EditModuleDialogProps {
  module: Module;
  provider: ModuleProviderInfo;
  onClose: () => void;
}

export default function EditModuleDialog(props: EditModuleDialogProps) {
  const { t } = useTranslation();
  const api = useModulesApi();
  const query = useQuery({
    queryKey: queryKeys.moduleParameters(props.module.id),
    queryFn: () => api.getParameters(props.module.id),
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: false,
  });

  if (query.data && !query.isError) {
    return <ModuleParameterEditor {...props} initial={query.data} />;
  }

  return (
    <Dialog open onClose={props.onClose} fullWidth maxWidth="sm" scroll="paper">
      <DialogTitle>{t("moduleEdit.title", { tag: props.module.tag })}</DialogTitle>
      {query.isPending ? (
        <DialogContent><CircularProgress aria-label={t("common.loading")} /></DialogContent>
      ) : query.isError && (
        <>
          <DialogContent><Alert severity="error">{getApiErrorMessage(query.error, t("moduleEdit.loadFailed"))}</Alert></DialogContent>
          <DialogActions><Button color="inherit" onClick={props.onClose}>{t("common.cancel")}</Button></DialogActions>
        </>
      )}
    </Dialog>
  );
}

function ModuleParameterEditor({ module, provider, onClose, initial }: EditModuleDialogProps & { initial: ModuleParameters }) {
  const { t } = useTranslation();
  const api = useModulesApi();
  const queryClient = useQueryClient();
  const [parameters, setParameters] = useState(initial.parameters);
  const [passphrase, setPassphrase] = useState("");
  const [busy, setBusy] = useState(false);
  const [tested, setTested] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const prepareChanges = async () => {
    const changes = Object.fromEntries(
      Object.entries(parameters)
        .map(([name, value]) => [name, value.trim()] as const)
        .filter(([name, value]) => value !== initial.parameters[name] &&
          !(initial.storedSecrets.includes(name) && value === "")),
    );
    if (provider.id === SFTP_PROVIDER_ID && isEncryptedPuttyKey(changes.password ?? "")) {
      try {
        changes.password = await unlockPuttyKey(changes.password, passphrase.trim());
      } catch (caughtError) {
        const code = caughtError instanceof PuttyKeyError ? caughtError.code : "sftpKeyInvalid";
        throw new Error(t(`wizard.${code}`), { cause: caughtError });
      }
    }
    return changes;
  };

  const run = async (save: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const changes = await prepareChanges();
      if (save) {
        await api.updateParameters(module.id, changes);
        await queryClient.invalidateQueries({ queryKey: queryKeys.moduleParameters(module.id) });
        onClose();
      } else {
        setTested(false);
        await api.testParameters(module.id, changes);
        setTested(true);
      }
    } catch (caughtError) {
      if (caughtError instanceof Error) {
        setError(getApiErrorMessage(caughtError, t("moduleEdit.saveFailed")));
      }
    } finally {
      setBusy(false);
    }
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (tested && !busy) {
      void run(true);
    }
  };

  return (
    <Dialog open onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" scroll="paper">
      <DialogTitle>{t("moduleEdit.title", { tag: module.tag })}</DialogTitle>
      <Stack component="form" onSubmit={submit} minHeight={0}>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Typography variant="body2" color="text.secondary">{provider.name}</Typography>
            {module.destination === ModuleDestination.Target && (
              <Alert severity="warning">{t("moduleEdit.storageLocationWarning")}</Alert>
            )}
            <ParametersForm
              moduleMeta={provider}
              params={parameters}
              tag={module.tag}
              hideTag
              storedSecrets={initial.storedSecrets}
              privateKeyPassphrase={passphrase}
              onTagChange={() => {}}
              onParamChange={(name, value) => {
                setParameters((current) => ({ ...current, [name]: value }));
                setTested(false);
                setError(null);
                if (name === "password") {
                  setPassphrase("");
                }
              }}
              onPrivateKeyPassphraseChange={(value) => {
                setPassphrase(value);
                setTested(false);
              }}
              disabled={busy}
            />
            <Typography variant="body2" color="text.secondary">{t("moduleEdit.appliesNextTime")}</Typography>
            {tested && <Alert severity="success">{t("wizard.testSuccess")}</Alert>}
            {error && <Alert severity="error">{error}</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ flexWrap: "wrap" }}>
          <Button color="inherit" onClick={onClose} disabled={busy}>{t("common.cancel")}</Button>
          <Button color="inherit" onClick={() => void run(false)} disabled={busy}>{t("wizard.testConnection")}</Button>
          <Button type="submit" variant="contained" disabled={busy || !tested}
            startIcon={busy ? <CircularProgress size={16} /> : undefined}>
            {t("common.save")}
          </Button>
        </DialogActions>
      </Stack>
    </Dialog>
  );
}
