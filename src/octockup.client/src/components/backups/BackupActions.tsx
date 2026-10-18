import { Box, CircularProgress, IconButton, Tooltip } from "@mui/material";
import {
  BackupTable,
  DeleteOutline,
  FilterAlt,
  StopCircle,
} from "@mui/icons-material";
import { useTranslation } from "react-i18next";
import { confirm } from "material-ui-confirm";
import type { BackupItem, ScheduleReport } from "../../types/api";
import { BackupStatus } from "../../types/api";
import type { BackupOverallStatus } from "../../utils/backupUtils";
import { parseInterval } from "../../utils/scheduleUtils";
import { BackupRunMenu } from "./BackupRunMenu";

interface BackupActionsProps {
  backup: BackupItem;
  scheduleReports: Map<string, ScheduleReport>;
  isCanceling: boolean;
  isDeleting: boolean;
  isSavingIgnoredPaths: boolean;
  isScheduling: boolean;
  isStarting: boolean;
  status: BackupOverallStatus;
  onNavigateToSnapshots: () => void;
  onEditIgnoredPaths: () => void;
  onRunOnce: () => Promise<void>;
  onSetSchedule: (intervalMinutes: number) => Promise<void>;
  onDisableSchedule: () => Promise<void>;
  onCancel: (scheduleId: string) => Promise<void>;
  onDelete: () => Promise<void>;
}

export function BackupActions({
  backup,
  scheduleReports,
  isCanceling,
  isDeleting,
  isSavingIgnoredPaths,
  isScheduling,
  isStarting,
  status,
  onNavigateToSnapshots,
  onEditIgnoredPaths,
  onRunOnce,
  onSetSchedule,
  onDisableSchedule,
  onCancel,
  onDelete,
}: BackupActionsProps) {
  const { t } = useTranslation();
  const deletionPending = Boolean(backup.deletionRequestedAt);
  const metadataBusy = deletionPending || backup.snapshots.some((snapshot) => snapshot.deletionRequestedAt);

  const reportForBackup = scheduleReports.get(backup.id);
  const isRunningFromReport = reportForBackup?.status === BackupStatus.Running;

  const activeSchedule = backup.schedules.find(
    (schedule) => schedule.status === BackupStatus.Running,
  );
  const runningScheduleId = activeSchedule?.id;

  const isRunning = isRunningFromReport || !!activeSchedule;
  const recurringSchedule = backup.schedules.find(
    (schedule) => schedule.interval !== null && schedule.interval !== undefined,
  );
  const intervalMinutes = recurringSchedule?.interval
    ? parseInterval(recurringSchedule.interval)
    : null;

  return (
    <Box
      display="flex"
      flexDirection={{ xs: "row", sm: "column" }}
      justifyContent="flex-end"
    >
      <Tooltip title={t("backups.showSnapshots")} placement="left">
        <IconButton
          size="small"
          aria-label={t("backups.showSnapshots")}
          onClick={onNavigateToSnapshots}
        >
          <BackupTable color="warning" />
        </IconButton>
      </Tooltip>
      <Tooltip title={t("backups.ignoredPaths")} placement="left">
        <IconButton
          size="small"
          aria-label={t("backups.ignoredPaths")}
          color="info"
          disabled={isSavingIgnoredPaths || metadataBusy}
          onClick={(e) => {
            e.stopPropagation();
            onEditIgnoredPaths();
          }}
        >
          <FilterAlt />
        </IconButton>
      </Tooltip>
      {isRunning ? (
        <IconButton
          size="small"
          aria-label={t("backups.stop")}
          color="error"
          disabled={isCanceling || !runningScheduleId || deletionPending}
          onClick={async (e) => {
            e.stopPropagation();
            if (runningScheduleId) {
              await onCancel(runningScheduleId);
            }
          }}
        >
          {isCanceling ? (
            <CircularProgress size={20} />
          ) : (
            <StopCircle />
          )}
        </IconButton>
      ) : (
        <BackupRunMenu
          disabled={status === "running" || metadataBusy}
          intervalMinutes={intervalMinutes}
          loading={isStarting || isScheduling}
          onRunNow={onRunOnce}
          onSetSchedule={onSetSchedule}
          onDisableSchedule={onDisableSchedule}
        />
      )}
      <IconButton
        size="small"
        aria-label={t("common.delete")}
        color="primary"
        disabled={isDeleting || (deletionPending && !backup.deletionError)}
        onClick={async (e) => {
          e.stopPropagation();
          const result = await confirm({
            title: t("backups.deleteTitle"),
            description: t("backups.deleteText"),
            confirmationText: t("common.delete"),
            cancellationText: t("common.cancel"),
            confirmationButtonProps: { color: "error" },
          });
          if (result.confirmed) {
            await onDelete();
          }
        }}
      >
        {isDeleting ? (
          <CircularProgress size={20} color="inherit" />
        ) : (
          <DeleteOutline />
        )}
      </IconButton>
    </Box>
  );
}
