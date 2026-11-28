import {
  Box,
  List,
  Paper,
  Stack,
  Button,
  ListItem,
  Typography,
  IconButton,
  ListItemIcon,
  ListItemText,
  ListItemButton,
  CircularProgress,
} from "@mui/material";
import { ArrowRight, ArrowUpward, Folder, Home } from "@mui/icons-material";
import { useTranslation } from "react-i18next";

interface DirectoryBrowserProps {
  browserPath: string;
  browserDirs: string[];
  browserLoading: boolean;
  onNavigateToRoot: () => void;
  onNavigateUp: () => void;
  onNavigateToDir: (dir: string) => void;
  disabled?: boolean;
}

export function DirectoryBrowser({
  browserPath,
  browserDirs,
  browserLoading,
  onNavigateToRoot,
  onNavigateUp,
  onNavigateToDir,
  disabled,
}: DirectoryBrowserProps) {
  const { t } = useTranslation();

  return (
    <Paper sx={{ display: "flex", flexDirection: "column", flex: 1, gap: 1, p: 2 }}>
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
      >
        <Box display="flex" alignItems="center" gap={0.5} minWidth={0}>
          <IconButton
            size="small"
            aria-label={t("wizard.openRootDirectory")}
            onClick={onNavigateToRoot}
            disabled={disabled}
          >
            <Home fontSize="small" />
          </IconButton>
          <ArrowRight fontSize="small" />
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{ overflowWrap: "anywhere" }}
            >
            {browserPath || "/"}
          </Typography>
        </Box>
        {browserPath && (
          <Button
            size="small"
            color="inherit"
            startIcon={<ArrowUpward />}
            onClick={onNavigateUp}
            disabled={disabled}
          >
            {t("wizard.up")}
          </Button>
        )}
      </Stack>
      <Box flex={1} sx={{ contain: "size", minHeight: (theme) => theme.spacing(16), overflowY: "auto" }}>
        {browserLoading ? (
          <Box display="flex" justifyContent="center" p={2}>
            <CircularProgress size={24} />
          </Box>
        ) : browserDirs.length === 0 ? (
          <Typography
            variant="body2"
            color="text.secondary"
            textAlign="center"
            py={2}
          >
            {browserPath
              ? t("wizard.noSubdirectories")
              : t("wizard.clickToLoad")}
          </Typography>
        ) : (
          <List dense>
            {browserDirs.map((dir) => (
              <ListItem key={dir} disablePadding>
                <ListItemButton
                  onClick={() => onNavigateToDir(dir)}
                  disabled={disabled}
                >
                  <ListItemIcon sx={{ minWidth: 36 }}>
                    <Folder />
                  </ListItemIcon>
                  <ListItemText primary={dir} sx={{ overflowWrap: "anywhere" }} />
                </ListItemButton>
              </ListItem>
            ))}
          </List>
        )}
      </Box>
    </Paper>
  );
}
