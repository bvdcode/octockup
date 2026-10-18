import {
  Box,
  Card,
  Stack,
  Alert,
  Button,
  Typography,
  CardContent,
  CircularProgress,
  Snackbar,
  Chip,
  Tooltip,
} from "@mui/material";
import {
  DataGrid,
  type GridColDef,
  type GridRowParams,
} from "@mui/x-data-grid";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ArrowBack } from "@mui/icons-material";
import { formatSize } from "../utils/formatUtils";
import { useNavigate, useParams } from "react-router-dom";
import { useSnapshotsApi } from "../api/snapshotsApi";
import type { SnapshotDto } from "../types/api";
import { useAuthStore } from "@bvdcode/react-kit";
import { confirm } from "material-ui-confirm";
import SnapshotActionsCell from "../components/snapshots/SnapshotActionsCell";
import { getApiErrorMessage } from "../utils/apiError";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../query/queryKeys";
import { useSignalR } from "../hooks/useSignalR";
import { deletionRefreshInterval, useDeletionUpdates } from "../hooks/useDeletionUpdates";

export default function SnapshotsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { backupId } = useParams<{ backupId: string }>();
  const snapshotsApi = useSnapshotsApi();
  const queryClient = useQueryClient();
  const { connection } = useSignalR("/api/v1/event-hub");
  useDeletionUpdates(connection);
  const accessToken = useAuthStore((s) => s.accessToken);
  const snapshotsQuery = useQuery({
    queryKey: queryKeys.snapshots(backupId ?? ""),
    queryFn: () => {
      if (!backupId) {
        throw new Error(t("snapshots.missingBackupId"));
      }
      return snapshotsApi.listByBackup(backupId);
    },
    enabled: Boolean(backupId),
    refetchInterval: (query) => query.state.data?.some((snapshot) => snapshot.deletionRequestedAt)
      ? deletionRefreshInterval : false,
  });
  const snapshots = snapshotsQuery.data ?? [];
  const [copyMessage, setCopyMessage] = useState<string | null>(null);
  const [downloadRequested, setDownloadRequested] = useState(false);
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<string>>(
    () => new Set(),
  );
  const [actionError, setActionError] = useState<string | null>(null);
  const loadError = backupId
    ? snapshotsQuery.error
    : new Error(t("snapshots.missingBackupId"));

  const createSnapshotDownloadUrl = (
    snapshotId: string,
    validate: boolean,
  ) => {
    const url = new URL(
      `/api/v1/snapshots/${snapshotId}/download`,
      window.location.origin,
    );
    url.searchParams.set("access_token", accessToken || "");
    url.searchParams.set("validate", String(validate));
    return url.toString();
  };

  const handleDownload = (snapshotId: string, validate: boolean) => {
    setDownloadRequested(true);
    const link = document.createElement("a");
    link.href = createSnapshotDownloadUrl(snapshotId, validate);
    link.download = "";
    link.click();
  };

  const handleCopyDownloadLink = async (
    snapshotId: string,
    validate: boolean,
  ) => {
    await navigator.clipboard.writeText(
      createSnapshotDownloadUrl(snapshotId, validate),
    );
    setCopyMessage(
      t(validate ? "snapshots.validatedLinkCopied" : "snapshots.linkCopied"),
    );
  };

  const handleDelete = async (snapshot: SnapshotDto) => {
    const result = await confirm({
      title: t("snapshots.deleteTitle"),
      description: t("snapshots.deleteText"),
      confirmationText: t("common.delete"),
      cancellationText: t("common.cancel"),
      confirmationButtonProps: { color: "error" },
    });
    if (!result.confirmed) {
      return;
    }

    setDeletingIds((current) => {
      const next = new Set(current);
      next.add(snapshot.id);
      return next;
    });
    setActionError(null);
    try {
      await snapshotsApi.deleteSnapshot(snapshot.id);
      queryClient.setQueryData<SnapshotDto[]>(
        queryKeys.snapshots(backupId ?? ""),
        (current) =>
          (current ?? []).map((item) => item.id === snapshot.id
            ? { ...item, deletionRequestedAt: item.deletionRequestedAt ?? new Date().toISOString(), deletionError: null }
            : item),
      );
      await queryClient.invalidateQueries({ queryKey: queryKeys.snapshots(backupId ?? "") });
      await queryClient.invalidateQueries({ queryKey: queryKeys.backups });
    } catch (caughtError) {
      if (caughtError instanceof Error) {
        setActionError(
          getApiErrorMessage(caughtError, t("snapshots.deleteFailed")),
        );
      }
    } finally {
      setDeletingIds((current) => {
        const next = new Set(current);
        next.delete(snapshot.id);
        return next;
      });
    }
  };

  const columns: GridColDef<SnapshotDto>[] = [
    {
      field: "completedAt",
      headerName: t("snapshots.completedAt"),
      flex: 1,
      minWidth: 200,
      valueGetter: (value: string | null) =>
        value ? new Date(value).getTime() : 0,
      renderCell: (params) => params.row.deletionRequestedAt ? (
        <Tooltip title={t(params.row.deletionError ? "deletion.failedHelp" : "deletion.help")}>
          <Chip size="small" variant="outlined"
            color={params.row.deletionError ? "error" : "default"}
            sx={params.row.deletionError ? { bgcolor: "error.dark", color: "common.white" } : undefined}
            label={t(params.row.deletionError ? "deletion.failed" : "deletion.pending")} />
        </Tooltip>
      ) : params.row.completedAt
          ? new Date(params.row.completedAt).toLocaleString()
          : t("snapshots.never"),
    },
    {
      field: "filesCount",
      headerName: t("snapshots.filesCount"),
      flex: 1,
      minWidth: 150,
      align: "right",
      headerAlign: "right",
      valueFormatter: (value: number) => value.toLocaleString(),
    },
    {
      field: "totalSize",
      headerName: t("snapshots.totalSize"),
      flex: 1,
      minWidth: 150,
      align: "right",
      headerAlign: "right",
      valueFormatter: (value: number) => formatSize(value),
    },
    {
      field: "actions",
      headerName: t("snapshots.actions"),
      width: 112,
      sortable: false,
      filterable: false,
      align: "center",
      headerAlign: "center",
      cellClassName: "snapshot-actions-cell",
      renderCell: (params) => (
        <SnapshotActionsCell
          downloadDisabled={!accessToken || !params.row.completedAt || Boolean(params.row.deletionRequestedAt)}
          deleting={deletingIds.has(params.row.id) || Boolean(params.row.deletionRequestedAt && !params.row.deletionError)}
          onDownload={(validate) => handleDownload(params.row.id, validate)}
          onCopyLink={(validate) =>
            handleCopyDownloadLink(params.row.id, validate)
          }
          onDelete={() => handleDelete(params.row)}
        />
      ),
    },
  ];

  const handleRowClick = (params: GridRowParams<SnapshotDto>) => {
    if (params.row.deletionRequestedAt) {
      return;
    }
    navigate(`/backups/${backupId}/snapshots/${params.row.id}/files`);
  };

  if (loadError && snapshots.length === 0) {
    return (
      <Box p={2}>
        <Alert severity="error">
          {getApiErrorMessage(loadError, t("snapshots.loadFailed"))}
        </Alert>
      </Box>
    );
  }

  return (
    <Stack spacing={3} display="flex" flexDirection="column" flex={1}>
      <Snackbar
        open={downloadRequested}
        autoHideDuration={6000}
        onClose={() => setDownloadRequested(false)}
        message={t("snapshots.downloadRequested")}
      />
      {loadError && (
        <Alert severity="error">
          {getApiErrorMessage(loadError, t("snapshots.loadFailed"))}
        </Alert>
      )}
      {actionError && <Alert severity="error">{actionError}</Alert>}
      {copyMessage && (
        <Alert severity="success" onClose={() => setCopyMessage(null)}>
          {copyMessage}
        </Alert>
      )}
      <Box display="flex" alignItems="center" gap={2}>
        <Button
          variant="outlined"
          startIcon={<ArrowBack />}
          onClick={() => navigate("/backups")}
        >
          {t("common.back")}
        </Button>
        <Typography variant="h5">{t("snapshots.title")}</Typography>
      </Box>
      {snapshotsQuery.isPending && snapshots.length === 0 ? (
        <Box display="flex" justifyContent="center" p={4}>
          <CircularProgress />
        </Box>
      ) : snapshots.length === 0 ? (
        <Card>
          <CardContent>
            <Typography color="text.secondary">
              {t("snapshots.noSnapshots")}
            </Typography>
          </CardContent>
        </Card>
      ) : (
        <Box flex={1}>
          <DataGrid
            rows={snapshots}
            columns={columns}
            pageSizeOptions={[10, 25, 50, 100]}
            autoPageSize
            pagination
            initialState={{
              sorting: {
                sortModel: [{ field: "completedAt", sort: "desc" }],
              },
            }}
            onRowClick={handleRowClick}
            disableRowSelectionOnClick
            sx={{
              cursor: "pointer",
              "& .MuiDataGrid-row:hover": {
                cursor: "pointer",
              },
              "& .snapshot-actions-cell": {
                overflow: "visible",
              },
            }}
          />
        </Box>
      )}
    </Stack>
  );
}
