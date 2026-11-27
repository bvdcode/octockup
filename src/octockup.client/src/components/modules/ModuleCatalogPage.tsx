import { AddCircleOutline, DeleteOutline, EditOutlined } from "@mui/icons-material";
import {
  Alert,
  Box,
  Card,
  CardContent,
  CircularProgress,
  Divider,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from "@mui/material";
import { confirm } from "material-ui-confirm";
import { useSnackbar } from "notistack";
import { useState } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { getSourceIcon } from "../../constants/sourceIcons";
import { useModuleCatalog } from "../../hooks/useModuleCatalog";
import type { Module, ModuleProviderInfo } from "../../types/api";
import { ModuleDestination } from "../../types/api";
import { getApiErrorMessage } from "../../utils/apiError";
import { parseUtcDate } from "../../utils/dateUtils";
import { EditableModuleTag } from "../EditableModuleTag";
import EditModuleDialog from "./EditModuleDialog";

interface ModuleCatalogPageProps {
  destination: ModuleDestination;
  providerType: "source" | "storage";
  route: "/sources" | "/storages";
  translationPrefix: "sources" | "storages";
  emptyKey: "noSources" | "noStorages";
}

const cardGrid = {
  display: "grid",
  gridTemplateColumns: {
    xs: "repeat(auto-fill, minmax(140px, 1fr))",
    sm: "repeat(auto-fill, minmax(160px, 1fr))",
  },
  gap: 2,
};

export function ModuleCatalogPage({
  destination,
  providerType,
  route,
  translationPrefix,
  emptyKey,
}: ModuleCatalogPageProps) {
  const { t } = useTranslation();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const catalog = useModuleCatalog(destination, providerType);
  const [editing, setEditing] = useState<Module | null>(null);
  const editingProvider = catalog.providers.find((provider) => provider.id === editing?.backupModuleId);
  const key = (suffix: string) => `${translationPrefix}.${suffix}`;

  const renameModule = async (moduleId: string, newTag: string) => {
    try {
      await catalog.renameModule(moduleId, newTag);
    } catch (caughtError) {
      if (caughtError instanceof Error) {
        enqueueSnackbar(getApiErrorMessage(caughtError, t(key("renameFailed"))), { variant: "error" });
      }
      throw caughtError;
    }
  };

  if (catalog.isPending) {
    return (
      <Box display="flex" justifyContent="center" p={4}>
        <CircularProgress />
      </Box>
    );
  }

  if (catalog.error && !catalog.hasData) {
    return (
      <Box p={2}>
        <Alert severity="error">
          {getApiErrorMessage(catalog.error, t(key("loadFailed")))}
        </Alert>
      </Box>
    );
  }

  return (
    <Stack spacing={3}>
      <Box>
        <Typography variant="h5" gutterBottom>
          {t(key("title"))}
        </Typography>
        {catalog.modules.length === 0 ? (
          <Card>
            <CardContent>
              <Typography color="text.secondary">
                {t(key(emptyKey))}
              </Typography>
            </CardContent>
          </Card>
        ) : (
          <Box sx={cardGrid}>
            {catalog.modules.map((module) => (
              <ConfiguredModuleCard
                key={module.id}
                module={module}
                deleteTooltip={t(key("deleteTooltip"))}
                deleteTitle={t(key("deleteTitle"))}
                deleteText={t(key("deleteText"))}
                onRename={(newTag) => renameModule(module.id, newTag)}
                onDelete={() => catalog.deleteModule(module.id)}
                onEdit={() => setEditing(module)}
              />
            ))}
          </Box>
        )}
      </Box>
      <Divider />
      <Box>
        <Typography variant="h6" gutterBottom>
          {t(key("addNew"))}
        </Typography>
        <Box sx={cardGrid}>
          {catalog.providers.map((provider) => (
            <AvailableModuleCard
              key={provider.id}
              provider={provider}
              onClick={() =>
                navigate(`${route}/new?type=${encodeURIComponent(provider.id)}`)
              }
            />
          ))}
          {catalog.providers.length === 0 && (
            <Stack
              direction="row"
              spacing={1}
              alignItems="center"
              color="text.secondary"
            >
              <AddCircleOutline />
              <Typography variant="body2">
                {t(key("noTypesAvailable"))}
              </Typography>
            </Stack>
          )}
        </Box>
      </Box>
      {editing && editingProvider && (
        <EditModuleDialog key={editing.id} module={editing} provider={editingProvider} onClose={() => setEditing(null)} />
      )}
    </Stack>
  );
}

interface ConfiguredModuleCardProps {
  module: Module;
  deleteTooltip: string;
  deleteTitle: string;
  deleteText: string;
  onRename: (newTag: string) => Promise<void>;
  onDelete: () => Promise<void>;
  onEdit: () => void;
}

function ConfiguredModuleCard({
  module,
  deleteTooltip,
  deleteTitle,
  deleteText,
  onRename,
  onDelete,
  onEdit,
}: ConfiguredModuleCardProps) {
  const { t } = useTranslation();
  const deleteConfiguredModule = async () => {
    const result = await confirm({
      title: deleteTitle,
      description: deleteText,
      confirmationText: t("common.delete"),
      cancellationText: t("common.cancel"),
      confirmationButtonProps: { color: "error" },
    });
    if (result.confirmed) {
      await onDelete();
    }
  };

  return (
    <Card
      sx={{
        height: 140,
        display: "flex",
        alignItems: "stretch",
        justifyContent: "center",
        position: "relative",
      }}
    >
      <Tooltip title={t("moduleEdit.edit")}>
        <IconButton size="small" aria-label={t("moduleEdit.edit")}
          sx={{ position: "absolute", top: 4, right: 36 }} onClick={onEdit}>
          <EditOutlined fontSize="small" color="primary" />
        </IconButton>
      </Tooltip>
      <Tooltip title={deleteTooltip} placement="left">
        <IconButton
          size="small"
          aria-label={t("common.delete")}
          sx={{ position: "absolute", top: 4, right: 4 }}
          onClick={(event) => {
            event.stopPropagation();
            void deleteConfiguredModule();
          }}
        >
          <DeleteOutline fontSize="small" color="primary" />
        </IconButton>
      </Tooltip>
      <ModuleCardContent
        icon={<Box sx={{ fontSize: 32 }}>{getSourceIcon(module.backupModuleId)}</Box>}
      >
        <EditableModuleTag tag={module.tag} onRename={onRename} />
        <Typography
          variant="caption"
          noWrap
          sx={{
            textAlign: "center",
            maxWidth: 140,
            color: "text.secondary",
            fontSize: "0.7rem",
          }}
        >
          {parseUtcDate(module.createdAt)?.toLocaleDateString()}
        </Typography>
      </ModuleCardContent>
    </Card>
  );
}

interface AvailableModuleCardProps {
  provider: ModuleProviderInfo;
  onClick: () => void;
}

function AvailableModuleCard({ provider, onClick }: AvailableModuleCardProps) {
  return (
    <Card
      sx={{
        height: 140,
        cursor: "pointer",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        "&:hover": { bgcolor: "action.hover" },
      }}
      onClick={onClick}
    >
      <ModuleCardContent
        icon={<Box sx={{ fontSize: 32 }}>{getSourceIcon(provider.id)}</Box>}
      >
        <Typography
          variant="caption"
          noWrap
          sx={{ textAlign: "center", maxWidth: 140 }}
        >
          {provider.name}
        </Typography>
      </ModuleCardContent>
    </Card>
  );
}

interface ModuleCardContentProps {
  icon: ReactNode;
  children: ReactNode;
}

function ModuleCardContent({ icon, children }: ModuleCardContentProps) {
  return (
    <CardContent
      sx={{
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        gap: 0.5,
        justifyContent: "space-between",
        height: "100%",
        p: 2,
      }}
    >
      {icon}
      {children}
    </CardContent>
  );
}
