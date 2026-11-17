import React from "react";
import { Avatar } from "@mui/material";
import { Email, Folder, HelpOutline, LibraryMusic } from "@mui/icons-material";

// Mapping of backend source IDs to icons. Extend manually as needed.
const ICONS: Record<string, React.ReactNode> = {
  "Octockup.Server.Modules.IMAPSource": <Email />,
  "Octockup.Server.Modules.YandexMusicSource": <LibraryMusic />,
  "Octockup.Server.Modules.FileSystemBackupSource": <Folder />,
  "Octockup.Server.Modules.S3BackupStorage": (
    <Avatar src="/assets/s3.svg" sx={{ p: 1 }} />
  ),
  "Octockup.Server.Modules.SFTPBackupStorage": (
    <Avatar src="/assets/sftp.png" sx={{ p: 1 }} />
  ),
};

export function getSourceIcon(id: string): React.ReactNode {
  return ICONS[id] ?? <HelpOutline />;
}

export function listKnownSourceIds(): string[] {
  return Object.keys(ICONS);
}
