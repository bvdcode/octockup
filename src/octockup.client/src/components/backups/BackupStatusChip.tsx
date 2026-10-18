import { Box, Chip, Tooltip } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { BackupItem, ScheduleReport } from "../../types/api";
import { BackupStatus } from "../../types/api";
import { getBackupOverallStatus } from "../../utils/backupUtils";
import { getStatusChipColors } from "../../utils/themeColors";

interface BackupStatusChipProps {
  backup: BackupItem;
  scheduleToBackupMap: Record<string, string>;
  scheduleReports: Map<string, ScheduleReport>;
}

export function BackupStatusChip({
  backup,
  scheduleToBackupMap,
  scheduleReports,
}: BackupStatusChipProps) {
  const { t } = useTranslation();
  if (backup.deletionRequestedAt) {
    return (
      <Tooltip title={t(backup.deletionError ? "deletion.failedHelp" : "deletion.help")}>
        <Chip size="small" variant="outlined" color={backup.deletionError ? "error" : "default"}
          sx={backup.deletionError ? { bgcolor: "error.dark", color: "common.white" } : undefined}
          label={t(backup.deletionError ? "deletion.failed" : "deletion.pending")} />
      </Tooltip>
    );
  }
  const status = getBackupOverallStatus(
    backup,
    scheduleToBackupMap,
    scheduleReports,
  );

  const statusColors = {
    running: "info",
    failed: "error",
    warning: "warning",
    scheduled: "warning",
    success: "success",
    idle: "default",
    created: "default",
  } as const;

  const hasFailedRun = status === "failed" || status === "warning";
  let errorMessage = "";
  if (hasFailedRun) {
    const failedSchedule = (backup.schedules || [])
      .filter(
        (schedule) =>
          schedule.status === BackupStatus.Failed && schedule.errorMessage,
      )
      .sort(
        (left, right) =>
          new Date(right.finishedAt || right.startAt).getTime() -
          new Date(left.finishedAt || left.startAt).getTime(),
      )[0];
    errorMessage = failedSchedule?.errorMessage || t("backups.unknownError");
  }

  const chip = (
    <Chip
      label={t(`backupStatus.${status}`)}
      size="small"
      color={statusColors[status]}
      sx={(theme) => {
        const accessibleStyle = getStatusChipColors(status, theme);
        const baseStyle = { height: 20, fontSize: "0.7rem" };
        if (accessibleStyle === null) {
          return baseStyle;
        }
        return {
          ...baseStyle,
          ...accessibleStyle,
        };
      }}
    />
  );

  if (hasFailedRun && errorMessage) {
    return (
      <Tooltip title={errorMessage} placement="top" arrow>
        <Box component="span">{chip}</Box>
      </Tooltip>
    );
  }

  return chip;
}
