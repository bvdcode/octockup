import { Download, Folder, InsertDriveFile } from "@mui/icons-material";
import { Box, IconButton, List, ListItem, ListItemButton, ListItemIcon, ListItemText, Tooltip, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { BrowserEntry } from "../../types/storageBrowser";
import { formatSize } from "../../utils/formatUtils";
interface Props {
  entries: BrowserEntry[];
  onOpen: (path: string) => void;
  onDownload: (entry: BrowserEntry) => void;
}
export default function BrowserFileList({ entries, onOpen, onDownload }: Props) {
  const { t } = useTranslation();
  return (
    <List disablePadding>
      {entries.map((entry) => (
        <ListItem key={entry.path} disablePadding divider secondaryAction={
          <Tooltip title={t("storageBrowser.download")}>
            <IconButton aria-label={t("storageBrowser.downloadItem", { name: entry.name })}
              onClick={() => onDownload(entry)}><Download /></IconButton>
          </Tooltip>
        }>
          {entry.isDirectory ? (
            <ListItemButton onClick={() => onOpen(entry.path)}>
              <ListItemIcon><Folder /></ListItemIcon>
              <ListItemText primary={entry.name} slotProps={{ primary: { sx: { overflowWrap: "anywhere" } } }} />
            </ListItemButton>
          ) : (
            <Box display="flex" alignItems="center" py={1} px={2} pr={7} gap={2} minWidth={0} flex={1}>
              <InsertDriveFile color="action" />
              <Box minWidth={0} flex={1}>
                <Typography sx={{ overflowWrap: "anywhere" }}>{entry.name}</Typography>
                <Typography variant="body2" color="text.secondary">{formatSize(entry.size)}</Typography>
              </Box>
            </Box>
          )}
        </ListItem>
      ))}
    </List>
  );
}
