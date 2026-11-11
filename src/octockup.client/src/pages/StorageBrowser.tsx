import { ArrowUpward, Download, Lock, Refresh } from "@mui/icons-material";
import { Alert, Box, Button, CircularProgress, MenuItem, Pagination, Paper, Stack, TextField, Typography } from "@mui/material";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuthStore } from "@bvdcode/react-kit";
import { useModulesApi } from "../api/modulesApi";
import { useStorageBrowserApi } from "../api/storageBrowserApi";
import BrowserFileList from "../components/storage-browser/BrowserFileList";
import { ModuleDestination } from "../types/api";
import type { BrowserEntry, BrowserSession } from "../types/storageBrowser";
import { queryKeys } from "../query/queryKeys";
import { getApiErrorMessage } from "../utils/apiError";

export default function StorageBrowserPage() {
  const { t, i18n } = useTranslation();
  const api = useStorageBrowserApi();
  const modulesApi = useModulesApi();
  const queries = useQueryClient();
  const token = useAuthStore((state) => state.accessToken);
  const [target, setTarget] = useState("");
  const [masterKey, setMasterKey] = useState("");
  const [session, setSession] = useState<BrowserSession | null>(null);
  const [snapshot, setSnapshot] = useState("");
  const [path, setPath] = useState("");
  const [page, setPage] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const activeSession = useRef<string | null>(null);
  const modules = useQuery({ queryKey: queryKeys.modules, queryFn: () => modulesApi.list() });
  const discovery = useQuery({
    queryKey: ["storage-browser-discovery", target],
    queryFn: () => api.discover(target),
    enabled: !!target,
    refetchInterval: (query) => ["running", "queued"].includes(query.state.data?.publication.status ?? "") ? 2000 : false,
  });
  const directory = useQuery({
    queryKey: ["storage-browser-directory", session?.session, snapshot, path, page],
    queryFn: () => api.directory(session!.session, snapshot, path, page),
    enabled: !!session && !!snapshot,
    gcTime: 0,
    retry: false,
  });
  useEffect(() => () => {
    if (activeSession.current) {
      void api.close(activeSession.current).catch(() => {});
    }
  }, [api]);
  const close = () => {
    if (activeSession.current) {
      void api.close(activeSession.current).catch(() => {});
    }
    activeSession.current = null;
    setSession(null);
    setSnapshot("");
    setPath("");
    setPage(0);
    setMasterKey("");
    setError("");
    queries.removeQueries({ queryKey: ["storage-browser-directory"] });
  };
  const openPath = (next: string) => { setPath(next); setPage(0); };
  const unlock = async () => {
    setBusy(true);
    setError("");
    try {
      const opened = await api.unlock(target, masterKey);
      activeSession.current = opened.session;
      setSession(opened);
      setSnapshot(opened.snapshots[0]?.id ?? "");
      setMasterKey("");
    } catch (caught) {
      if (caught instanceof Error) {
        setError(getApiErrorMessage(caught, t("storageBrowser.unlockFailed")));
      }
    } finally { setBusy(false); }
  };
  const publish = async () => {
    setBusy(true);
    setError("");
    try {
      await api.publish(target);
      await discovery.refetch();
    } catch (caught) {
      if (caught instanceof Error) {
        setError(getApiErrorMessage(caught, t("storageBrowser.publishFailed")));
      }
    } finally { setBusy(false); }
  };
  const download = (entry?: BrowserEntry) => {
    if (!session) { return; }
    const form = document.createElement("form");
    form.method = "POST";
    form.action = `/api/v1/storage-browser/download?access_token=${encodeURIComponent(token ?? "")}`;
    form.target = "_blank";
    const values = { session: session.session, snapshotId: snapshot, path: entry?.path ?? path,
      page: String(page), folder: String(entry?.isDirectory ?? true) };
    for (const [name, value] of Object.entries(values)) {
      const input = document.createElement("input");
      input.type = "hidden"; input.name = name; input.value = value; form.appendChild(input);
    }
    document.body.appendChild(form); form.submit(); form.remove();
  };
  const loadError = modules.error ?? discovery.error ?? directory.error;
  return (
    <Box p={2} sx={{ "& .MuiInputLabel-root.Mui-focused": { color: "text.primary" } }}>
      <Stack spacing={3}>
        <Box>
          <Typography variant="h5">{t("storageBrowser.title")}</Typography>
          <Typography color="text.secondary">{t("storageBrowser.description")}</Typography>
        </Box>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
          <TextField select fullWidth label={t("storageBrowser.target")} value={target} disabled={busy}
            onChange={(event) => { close(); setTarget(event.target.value); }}>
            {(modules.data ?? []).filter((module) => module.destination === ModuleDestination.Target)
              .map((module) => <MenuItem key={module.id} value={module.id}>{module.tag}</MenuItem>)}
          </TextField>
          <Button color="inherit" startIcon={<Refresh />} disabled={!target || busy} sx={{ flexShrink: 0 }}
            onClick={() => void discovery.refetch()}>{t("common.refresh")}</Button>
        </Stack>
        {(error || loadError) && <Alert severity="error">{error || (loadError && getApiErrorMessage(loadError, t("storageBrowser.loadFailed")))}</Alert>}
        {discovery.isFetching && !discovery.data && <CircularProgress />}
        {discovery.data && !session && (
          <Paper variant="outlined">
            <Stack p={3} spacing={2}>
              <Alert severity={discovery.data.found ? "success" : "info"}>
                {t(discovery.data.found ? "storageBrowser.found" : "storageBrowser.notFound")}
              </Alert>
              {discovery.data.found && (
                <Box component="form" onSubmit={(event) => { event.preventDefault(); void unlock(); }}>
                  <Stack spacing={2}>
                    <TextField label={t("storageBrowser.masterKey")} type="password" value={masterKey}
                      autoComplete="off" fullWidth onChange={(event) => setMasterKey(event.target.value)} />
                    <Button type="submit" variant="contained" disabled={busy || !masterKey.trim()} startIcon={<Lock />}>
                      {t("storageBrowser.unlock")}
                    </Button>
                  </Stack>
                </Box>
              )}
              {discovery.data.canPublish && <>
                <Typography variant="body2" color="text.secondary">{t("storageBrowser.publishHelp")}</Typography>
                <Button color="inherit" disabled={busy || ["running", "queued"].includes(discovery.data.publication.status)}
                  onClick={() => void publish()}>{t("storageBrowser.publish")}</Button>
                {discovery.data.publication.status !== "idle" &&
                  <Typography role="status">{t(`storageBrowser.publication.${discovery.data.publication.status}`, {
                    completed: discovery.data.publication.completed, total: discovery.data.publication.total,
                  })}</Typography>}
              </>}
            </Stack>
          </Paper>
        )}
        {session && <>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={2} alignItems={{ sm: "center" }}>
            <TextField select fullWidth label={t("storageBrowser.snapshot")} value={snapshot}
              sx={{ "& .MuiSelect-select": { whiteSpace: "normal" } }}
              onChange={(event) => { setSnapshot(event.target.value); openPath(""); }}>
              {session.snapshots.map((item) => <MenuItem key={item.id} value={item.id}>
                {item.tag} · {new Date(item.completedAt).toLocaleString(i18n.resolvedLanguage ?? i18n.language)}
              </MenuItem>)}
            </TextField>
            <Button color="inherit" startIcon={<Lock />} sx={{ flexShrink: 0 }} onClick={close}>{t("storageBrowser.close")}</Button>
          </Stack>
          <Paper variant="outlined">
            <Stack direction="row" p={1} gap={1} alignItems="center" flexWrap="wrap">
              <Button color="inherit" startIcon={<ArrowUpward />} disabled={!path}
                onClick={() => openPath(path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "")}>{t("storageBrowser.up")}</Button>
              <Typography flex={1} minWidth={0} sx={{ overflowWrap: "anywhere", flexBasis: { xs: "100%", sm: "auto" },
                order: { xs: 1, sm: 0 } }}>{path || "/"}</Typography>
              <Button color="inherit" startIcon={<Download />} sx={{ ml: "auto", flexShrink: 0 }}
                onClick={() => download()}>{t("storageBrowser.folder")}</Button>
            </Stack>
            {directory.isFetching ? <Box p={3}><CircularProgress /></Box> :
              <BrowserFileList entries={directory.data?.entries ?? []} onOpen={openPath} onDownload={download} />}
            {directory.data?.total === 0 && <Typography p={3}>{t("storageBrowser.empty")}</Typography>}
            {directory.data && directory.data.pages > 1 && <Box display="flex" justifyContent="center" p={2}>
              <Pagination count={directory.data.pages} page={page + 1} onChange={(_, next) => setPage(next - 1)} size="small" />
            </Box>}
          </Paper>
          <Typography variant="caption" color="text.secondary">{t("storageBrowser.sessionHelp")}</Typography>
        </>}
      </Stack>
    </Box>
  );
}
