import { Box, Card, CardContent, Divider, Typography } from "@mui/material";
import { ArrowDownward } from "@mui/icons-material";
import { useNavigate } from "react-router-dom";
import type { BackupItem, ScheduleReport } from "../../types/api";
import { getBackupOverallStatus } from "../../utils/backupUtils";
import { getSourceIcon } from "../../constants/sourceIcons";
import { EditableModuleTag } from "../EditableModuleTag";
import { BackupStatusChip } from "./BackupStatusChip";
import { BackupMetadata } from "./BackupMetadata";
import { BackupProgress } from "./BackupProgress";
import { BackupActions } from "./BackupActions";

interface BackupCardProps {
  backup: BackupItem;
  scheduleToBackupMap: Record<string, string>;
  scheduleReports: Map<string, ScheduleReport>;
  isCanceling: boolean;
  isDeleting: boolean;
  isSavingIgnoredPaths: boolean;
  isScheduling: boolean;
  isStarting: boolean;
  onRename: (backupId: string, newTag: string) => Promise<void>;
  onEditIgnoredPaths: (backupId: string) => void;
  onRunOnce: (backupId: string) => Promise<void>;
  onSetSchedule: (backupId: string, intervalMinutes: number) => Promise<void>;
  onDisableSchedule: (backupId: string) => Promise<void>;
  onCancel: (scheduleId: string) => Promise<void>;
  onDelete: (backupId: string) => Promise<void>;
}

export function BackupCard({
  backup,
  scheduleToBackupMap,
  scheduleReports,
  isCanceling,
  isDeleting,
  isSavingIgnoredPaths,
  isScheduling,
  isStarting,
  onRename,
  onEditIgnoredPaths,
  onRunOnce,
  onSetSchedule,
  onDisableSchedule,
  onCancel,
  onDelete,
}: BackupCardProps) {
  const navigate = useNavigate();
  const status = getBackupOverallStatus(
    backup,
    scheduleToBackupMap,
    scheduleReports,
  );

  const report = scheduleReports.get(backup.id);

  return (
    <Card
      sx={(theme) => ({
        display: "flex",
        alignItems: { xs: "stretch", sm: "center" },
        position: "relative",
        minHeight: 80,
        borderLeft: `3px solid ${
          backup.deletionRequestedAt
            ? theme.palette.divider
            : status === "running"
            ? theme.palette.info.main
            : status === "failed"
            ? theme.palette.error.main
            : status === "warning"
            ? theme.palette.warning.main
            : status === "scheduled"
            ? theme.palette.warning.light
            : status === "success"
            ? theme.palette.success.main
            : status === "created"
            ? theme.palette.grey[500]
            : theme.palette.grey[300]
        }`,
      })}
    >
      <CardContent
        sx={{
          display: "flex",
          flexDirection: { xs: "column", sm: "row" },
          alignItems: { xs: "stretch", sm: "center" },
          gap: { xs: 1, sm: 2 },
          width: "100%",
          p: 2,
          "&:last-child": { pb: 2 },
        }}
      >
        <Box
          display="flex"
          alignItems="center"
          justifyContent="center"
          flexDirection="column"
          sx={{ display: { xs: "none", sm: "flex" } }}
        >
          <Box
            fontSize={36}
            sx={{
              width: 36,
              height: 36,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
            }}
          >
            {getSourceIcon(backup.source.backupModuleId)}
          </Box>
          <ArrowDownward />
          <Box
            fontSize={36}
            sx={{
              width: 36,
              height: 36,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
            }}
          >
            {getSourceIcon(backup.storage.backupModuleId)}
          </Box>
        </Box>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Box
            display="flex"
            alignItems={{ xs: "flex-start", sm: "center" }}
            flexDirection={{ xs: "column", sm: "row" }}
            justifyContent="space-between"
            gap={1}
          >
            <Box
              display="flex"
              alignItems="center"
              flexWrap="wrap"
              gap={1}
              minWidth={0}
            >
              {backup.deletionRequestedAt ? <Typography variant="subtitle2">{backup.tag}</Typography> : <EditableModuleTag
                tag={backup.tag}
                onRename={(newTag) => onRename(backup.id, newTag)}
              />}
              <Divider
                orientation="vertical"
                flexItem
                sx={{ display: { xs: "none", sm: "block" } }}
              />
              <Typography variant="caption" sx={{ color: "text.secondary" }}>
                {backup.source.tag} → {backup.storage.tag}
              </Typography>
            </Box>
            <BackupStatusChip
              backup={backup}
              scheduleToBackupMap={scheduleToBackupMap}
              scheduleReports={scheduleReports}
            />
          </Box>
          <BackupMetadata backup={backup} />
          {report && !backup.deletionRequestedAt && <BackupProgress report={report} />}
        </Box>
        <Divider
          orientation="vertical"
          flexItem
          sx={{ display: { xs: "none", sm: "block" } }}
        />
        <Divider sx={{ display: { xs: "block", sm: "none" } }} />
        <BackupActions
          backup={backup}
          scheduleReports={scheduleReports}
          isCanceling={isCanceling}
          isDeleting={isDeleting}
          isSavingIgnoredPaths={isSavingIgnoredPaths}
          isScheduling={isScheduling}
          isStarting={isStarting}
          status={status}
          onNavigateToSnapshots={() =>
            navigate(`/backups/${backup.id}/snapshots`)
          }
          onEditIgnoredPaths={() => onEditIgnoredPaths(backup.id)}
          onRunOnce={() => onRunOnce(backup.id)}
          onSetSchedule={(intervalMinutes) =>
            onSetSchedule(backup.id, intervalMinutes)
          }
          onDisableSchedule={() => onDisableSchedule(backup.id)}
          onCancel={onCancel}
          onDelete={() => onDelete(backup.id)}
        />
      </CardContent>
    </Card>
  );
}
