import { Alert, Button, CircularProgress, Stack } from "@mui/material";
import { CheckCircle } from "@mui/icons-material";
import { useTranslation } from "react-i18next";

interface TestActionsProps {
  testLoading: boolean;
  testMessage: string | null;
  testError: string | null;
  creating: boolean;
  moduleType: "source" | "storage" | "target";
  onTest: () => void;
}

export function TestActions({
  testLoading,
  testMessage,
  testError,
  creating,
  moduleType,
  onTest,
}: TestActionsProps) {
  const { t } = useTranslation();

  return (
    <Stack spacing={2}>
      {testMessage && (
        <Alert severity="success" icon={<CheckCircle />}>
          {testMessage}
        </Alert>
      )}
      {testError && <Alert severity="error">{testError}</Alert>}
      <Stack direction="row" spacing={2} justifyContent="flex-end">
        <Button
          variant="outlined"
          color="inherit"
          onClick={onTest}
          disabled={testLoading || creating}
        >
          {testLoading ? t("wizard.testing") : t("wizard.testConnection")}
        </Button>
        <Button
          type="submit"
          variant="contained"
          disabled={!testMessage || creating}
          startIcon={creating ? <CircularProgress size={20} /> : null}
        >
          {creating
            ? t("wizard.creating")
            : t(moduleType === "source" ? "sources.create" : "storages.create")}
        </Button>
      </Stack>
    </Stack>
  );
}
