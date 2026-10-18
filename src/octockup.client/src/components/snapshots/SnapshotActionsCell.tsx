import { useState } from "react";
import {
  Box,
  CircularProgress,
  Collapse,
  Divider,
  IconButton,
  Tooltip,
} from "@mui/material";
import {
  ContentCopy,
  DeleteOutline,
  Download,
  FactCheck,
  Verified,
} from "@mui/icons-material";
import { useTranslation } from "react-i18next";

interface SnapshotActionsCellProps {
  downloadDisabled: boolean;
  deleting: boolean;
  onDownload: (validate: boolean) => void;
  onCopyLink: (validate: boolean) => Promise<void>;
  onDelete: () => Promise<void>;
}

export default function SnapshotActionsCell({
  downloadDisabled,
  deleting,
  onDownload,
  onCopyLink,
  onDelete,
}: SnapshotActionsCellProps) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);

  const handleDownload = (validate: boolean) => {
    setExpanded(false);
    onDownload(validate);
  };

  const handleCopyLink = (validate: boolean) => {
    setExpanded(false);
    void onCopyLink(validate);
  };

  return (
    <Box
      display="flex"
      alignItems="center"
      justifyContent="flex-end"
      position="relative"
      width="100%"
      onClick={(event) => event.stopPropagation()}
    >
      <Collapse
        in={expanded && !downloadDisabled}
        orientation="horizontal"
        timeout="auto"
        unmountOnExit
        sx={{ position: "absolute", right: 80, zIndex: 1 }}
      >
        <Box
          display="flex"
          alignItems="center"
          gap={0.25}
          bgcolor="background.paper"
          p={0.5}
          borderRadius={1}
          boxShadow={2}
        >
          <Tooltip title={t("snapshots.download")}>
            <IconButton
              size="small"
              color="primary"
              aria-label={t("snapshots.download")}
              onClick={() => handleDownload(false)}
            >
              <Download />
            </IconButton>
          </Tooltip>
          <Tooltip title={t("snapshots.downloadValidated")}>
            <IconButton
              size="small"
              color="primary"
              aria-label={t("snapshots.downloadValidated")}
              onClick={() => handleDownload(true)}
            >
              <Verified />
            </IconButton>
          </Tooltip>
          <Divider orientation="vertical" flexItem />
          <Tooltip title={t("snapshots.copyLink")}>
            <IconButton
              size="small"
              color="primary"
              aria-label={t("snapshots.copyLink")}
              onClick={() => handleCopyLink(false)}
            >
              <ContentCopy />
            </IconButton>
          </Tooltip>
          <Tooltip title={t("snapshots.copyValidatedLink")}>
            <IconButton
              size="small"
              color="primary"
              aria-label={t("snapshots.copyValidatedLink")}
              onClick={() => handleCopyLink(true)}
            >
              <FactCheck />
            </IconButton>
          </Tooltip>
        </Box>
      </Collapse>

      <Tooltip title={t("snapshots.downloadOptions")}>
        <span>
          <IconButton
            size="small"
            color="primary"
            aria-label={t("snapshots.downloadOptions")}
            aria-expanded={expanded}
            disabled={downloadDisabled}
            onClick={() => setExpanded((current) => !current)}
          >
            <Download />
          </IconButton>
        </span>
      </Tooltip>
      <Tooltip title={t("snapshots.deleteTooltip")}>
        <span>
          <IconButton
            size="small"
            color="error"
            aria-label={t("snapshots.deleteTooltip")}
            disabled={deleting}
            onClick={() => void onDelete()}
          >
            {deleting ? (
              <CircularProgress size={20} color="inherit" />
            ) : (
              <DeleteOutline />
            )}
          </IconButton>
        </span>
      </Tooltip>
    </Box>
  );
}
