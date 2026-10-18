import {
  Alert,
  Box,
  Card,
  Stack,
  Typography,
  CardContent,
  CircularProgress,
} from "@mui/material";
import { useState, useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useSearchParams } from "react-router-dom";
import type { BackupItem } from "../types/api";
import { useBackupsApi } from "../api/backupsApi";
import { useSchedulesApi } from "../api/schedulesApi";
import { useSignalR } from "../hooks/useSignalR";
import { getBackupOverallStatus } from "../utils/backupUtils";
import { EditIgnoredPathsDialog } from "../components/EditIgnoredPathsDialog";
import { BackupCard } from "../components/backups/BackupCard";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../query/queryKeys";
import { BackupListToolbar } from "../components/backups/BackupListToolbar";
import { BackupListSummary } from "../components/backups/BackupListSummary";
import { BackupSortOption } from "../types/backupList";
import {
  filterBackups,
  getLatestCompletedSnapshot,
  parseBackupSortOption,
  sortBackups,
} from "../utils/backupListUtils";
import { usePendingIds } from "../hooks/usePendingIds";
import { useBackupScheduleReports } from "../hooks/useBackupScheduleReports";
import { getApiErrorMessage } from "../utils/apiError";
import { deletionRefreshInterval, useDeletionUpdates } from "../hooks/useDeletionUpdates";

export default function BackupsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const backupsApi = useBackupsApi();
  const schedulesApi = useSchedulesApi();
  const queryClient = useQueryClient();
  const { connection, isConnected } = useSignalR("/api/v1/event-hub");
  useDeletionUpdates(connection);
  const deleting = usePendingIds();
  const starting = usePendingIds();
  const canceling = usePendingIds();
  const savingIgnoredPaths = usePendingIds();
  const scheduling = usePendingIds();
  const backupsQuery = useQuery({
    queryKey: queryKeys.backups,
    queryFn: () => backupsApi.list(),
    refetchInterval: (query) => query.state.data?.some((backup) =>
      backup.deletionRequestedAt || backup.snapshots.some((snapshot) => snapshot.deletionRequestedAt),
    ) ? deletionRefreshInterval : false,
  });
  const backups = useMemo(
    () => backupsQuery.data ?? [],
    [backupsQuery.data],
  );
  const [editingIgnoredPathsId, setEditingIgnoredPathsId] = useState<
    string | null
  >(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const selectedStorageId = searchParams.get("storage");
  const search = searchParams.get("search") ?? "";
  const sort = parseBackupSortOption(searchParams.get("sort"));

  const updateBackups = useCallback(
    (updater: (current: BackupItem[]) => BackupItem[]) => {
      queryClient.setQueryData<BackupItem[]>(queryKeys.backups, (current) =>
        updater(current ?? []),
      );
    },
    [queryClient],
  );

  const reloadBackups = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.backups });
  }, [queryClient]);

  const scheduleReports = useBackupScheduleReports(
    connection,
    isConnected,
    updateBackups,
    reloadBackups,
  );

  const executeAction = async (
    backupId: string,
    run: (id: string, action: () => Promise<void>) => Promise<void>,
    action: () => Promise<void>,
  ) => {
    setActionError(null);
    try {
      await run(backupId, action);
    } catch (caughtError) {
      if (caughtError instanceof Error) {
        setActionError(
          getApiErrorMessage(caughtError, t("backups.actionFailed")),
        );
      }
    }
  };

  const updateSearchParameter = (key: string, value: string | null) => {
    const next = new URLSearchParams(searchParams);
    if (value) {
      next.set(key, value);
    } else {
      next.delete(key);
    }
    setSearchParams(next, { replace: true });
  };

  const scheduleToBackupMap = useMemo(() => {
    const mapping: Record<string, string> = {};
    backups.forEach((backup) => {
      backup.schedules?.forEach((schedule) => {
        mapping[schedule.id] = schedule.backupId;
      });
    });
    return mapping;
  }, [backups]);

  const handleRename = async (backupId: string, newTag: string) => {
    const trimmedTag = newTag.trim();
    await backupsApi.rename(backupId, trimmedTag);
    updateBackups((prev) =>
      prev.map((b) => (b.id === backupId ? { ...b, tag: trimmedTag } : b)),
    );
  };

  const handleSaveIgnoredPaths = async (backupId: string, paths: string[]) => {
    await executeAction(backupId, savingIgnoredPaths.run, async () => {
      await backupsApi.updateIgnoredPaths(backupId, paths);
      updateBackups((previous) =>
        previous.map((backup) =>
          backup.id === backupId ? { ...backup, ignoredPaths: paths } : backup,
        ),
      );
    });
  };

  const handleRunOnce = async (backupId: string) => {
    await executeAction(backupId, starting.run, async () => {
      await schedulesApi.runBackupNow(backupId);
      await Promise.all([
        reloadBackups(),
        queryClient.invalidateQueries({ queryKey: queryKeys.schedules }),
      ]);
    });
  };

  const handleSetSchedule = async (
    backupId: string,
    intervalMinutes: number,
  ) => {
    await executeAction(backupId, scheduling.run, async () => {
      await schedulesApi.setBackupSchedule(backupId, intervalMinutes);
      await Promise.all([
        reloadBackups(),
        queryClient.invalidateQueries({ queryKey: queryKeys.schedules }),
      ]);
    });
  };

  const handleDisableSchedule = async (backupId: string) => {
    await executeAction(backupId, scheduling.run, async () => {
      await schedulesApi.disableBackupSchedule(backupId);
      await Promise.all([
        reloadBackups(),
        queryClient.invalidateQueries({ queryKey: queryKeys.schedules }),
      ]);
    });
  };

  const handleCancel = async (scheduleId: string) => {
    const backupId = scheduleToBackupMap[scheduleId]
      ?? backups.find((backup) =>
        backup.schedules.some((schedule) => schedule.id === scheduleId),
      )?.id;
    if (!backupId) {
      return;
    }

    await executeAction(backupId, canceling.run, async () => {
      await schedulesApi.cancel(scheduleId);
      await reloadBackups();
    });
  };

  const handleDelete = async (backupId: string) => {
    await executeAction(backupId, deleting.run, async () => {
      await backupsApi.delete(backupId);
      updateBackups((previous) =>
        previous.map((backup) => backup.id === backupId
          ? { ...backup, deletionRequestedAt: backup.deletionRequestedAt ?? new Date().toISOString(), deletionError: null }
          : backup),
      );
      await reloadBackups();
      await queryClient.invalidateQueries({ queryKey: queryKeys.schedules });
    });
  };

  const uniqueStorages = useMemo(() => {
    const storageMap = new Map<string, { id: string; tag: string }>();
    backups.forEach((backup) => {
      if (!storageMap.has(backup.storageId)) {
        storageMap.set(backup.storageId, {
          id: backup.storageId,
          tag: backup.storage.tag,
        });
      }
    });
    return Array.from(storageMap.values()).sort((a, b) =>
      a.tag.localeCompare(b.tag),
    );
  }, [backups]);

  const filteredBackups = useMemo(
    () => filterBackups(backups, selectedStorageId, search),
    [backups, search, selectedStorageId],
  );
  const visibleBackups = useMemo(
    () =>
      sortBackups(filteredBackups, sort, (backup) =>
        getBackupOverallStatus(backup, scheduleToBackupMap, scheduleReports),
      ),
    [filteredBackups, scheduleReports, scheduleToBackupMap, sort],
  );
  const summary = useMemo(() => {
    let logicalSize = 0;
    let runningCount = 0;
    let issueCount = 0;
    filteredBackups.forEach((backup) => {
      logicalSize += getLatestCompletedSnapshot(backup)?.totalSize ?? 0;
      const status = getBackupOverallStatus(
        backup,
        scheduleToBackupMap,
        scheduleReports,
      );
      if (status === "running") {
        runningCount++;
      }
      if (status === "failed" || status === "warning") {
        issueCount++;
      }
    });
    return { logicalSize, runningCount, issueCount };
  }, [filteredBackups, scheduleReports, scheduleToBackupMap]);

  if (backupsQuery.isPending && backups.length === 0) {
    return (
      <Box display="flex" justifyContent="center" p={4}>
        <CircularProgress />
      </Box>
    );
  }

  return (
    <Stack spacing={1.5} mt={-2}>
      <BackupListToolbar
        search={search}
        selectedStorageId={selectedStorageId}
        sort={sort}
        storages={uniqueStorages}
        onCreate={() => navigate("/backups/new")}
        onSearchChange={(value) => updateSearchParameter("search", value)}
        onSortChange={(value) =>
          updateSearchParameter(
            "sort",
            value === BackupSortOption.Smart ? null : value,
          )
        }
        onStorageChange={(value) => updateSearchParameter("storage", value)}
      />
      {backupsQuery.error && (
        <Alert severity="error">
          {getApiErrorMessage(backupsQuery.error, t("backups.loadFailed"))}
        </Alert>
      )}
      {actionError && (
        <Alert severity="error" onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      )}
      {backups.length === 0 ? (
        <Card>
          <CardContent>
            <Typography color="text.secondary">
              {t("backups.noBackups")}
            </Typography>
          </CardContent>
        </Card>
      ) : visibleBackups.length === 0 ? (
        <Card>
          <CardContent>
            <Typography color="text.secondary">
              {t("backups.noMatches")}
            </Typography>
          </CardContent>
        </Card>
      ) : (
        <Stack spacing={1}>
          {visibleBackups.map((backup) => (
              <BackupCard
                key={backup.id}
                backup={backup}
                scheduleToBackupMap={scheduleToBackupMap}
                scheduleReports={scheduleReports}
                isCanceling={canceling.has(backup.id)}
                isDeleting={deleting.has(backup.id)}
                isSavingIgnoredPaths={savingIgnoredPaths.has(backup.id)}
                isScheduling={scheduling.has(backup.id)}
                isStarting={starting.has(backup.id)}
                onRename={handleRename}
                onEditIgnoredPaths={setEditingIgnoredPathsId}
                onRunOnce={handleRunOnce}
                onSetSchedule={handleSetSchedule}
                onDisableSchedule={handleDisableSchedule}
                onCancel={handleCancel}
                onDelete={handleDelete}
              />
            ))}
        </Stack>
      )}
      {visibleBackups.length > 0 && (
        <BackupListSummary
          backupCount={filteredBackups.length}
          issueCount={summary.issueCount}
          logicalSize={summary.logicalSize}
          runningCount={summary.runningCount}
        />
      )}
      {editingIgnoredPathsId &&
        (() => {
          const backup = backups.find((b) => b.id === editingIgnoredPathsId);
          if (!backup) return null;
          return (
            <EditIgnoredPathsDialog
              open={true}
              backupModuleId={backup.source.backupModuleId}
              initialPaths={backup.ignoredPaths}
              onClose={() => setEditingIgnoredPathsId(null)}
              onSave={(paths) =>
                handleSaveIgnoredPaths(editingIgnoredPathsId, paths)
              }
              loading={savingIgnoredPaths.has(editingIgnoredPathsId)}
            />
          );
        })()}
    </Stack>
  );
}
