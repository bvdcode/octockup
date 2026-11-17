import {
  Card,
  Stack,
  Checkbox,
  TextField,
  Typography,
  IconButton,
  CardContent,
  InputAdornment,
  FormControlLabel,
} from "@mui/material";
import { useState } from "react";
import type { ClipboardEvent } from "react";
import { useTranslation } from "react-i18next";
import type { ModuleProviderInfo } from "../../types/api";
import { Visibility, VisibilityOff } from "@mui/icons-material";
import { CHECKBOX_PARAMETERS } from "../../constants/checkboxParameters";
import {
  isEncryptedPuttyKey,
  SFTP_PROVIDER_ID,
} from "../../utils/puttyPrivateKey";

interface ParamState {
  [key: string]: string;
}

interface ParametersFormProps {
  moduleMeta: ModuleProviderInfo;
  params: ParamState;
  privateKeyPassphrase: string;
  tag: string;
  onParamChange: (name: string, value: string) => void;
  onPrivateKeyPassphraseChange: (value: string) => void;
  onTagChange: (value: string) => void;
  onParamsPaste?: (e: ClipboardEvent<HTMLInputElement>) => void;
  hideTag?: boolean;
  storedSecrets?: string[];
  disabled?: boolean;
}

export function ParametersForm({
  moduleMeta,
  params,
  privateKeyPassphrase,
  tag,
  onParamChange,
  onPrivateKeyPassphraseChange,
  onTagChange,
  onParamsPaste,
  hideTag = false,
  storedSecrets = [],
  disabled,
}: ParametersFormProps) {
  const { t } = useTranslation();
  const [showPassword, setShowPassword] = useState<Record<string, boolean>>({});

  const togglePasswordVisibility = (paramName: string) => {
    setShowPassword((prev) => ({
      ...prev,
      [paramName]: !prev[paramName],
    }));
  };

  return (
    <Card sx={{ "& .MuiInputLabel-root.Mui-focused": { color: "text.primary" } }}>
      <CardContent>
        <Typography variant="h6" gutterBottom>
          {t("wizard.parameters")}
        </Typography>
        <Stack spacing={2}>
          {!hideTag && <TextField
            required
            fullWidth
            label={t("wizard.tag")}
            value={tag}
            onChange={(e) => onTagChange(e.target.value)}
            placeholder={t("wizard.enterTag")}
            disabled={disabled}
          />}
          {moduleMeta.requiredParameters.length === 0 ? (
            <Typography
              variant="body2"
              color="text.secondary"
              fontStyle="italic"
            >
              {t("wizard.noParameters")}
            </Typography>
          ) : (
            <>
              {moduleMeta.requiredParameters.map((p) => {
                const isCheckbox = CHECKBOX_PARAMETERS.includes(p);

                if (isCheckbox) {
                  return (
                    <FormControlLabel
                      key={p}
                      control={
                        <Checkbox
                          checked={params[p] === "true"}
                          onChange={(e) =>
                            onParamChange(
                              p,
                              e.target.checked ? "true" : "false",
                            )
                          }
                          disabled={disabled}
                        />
                      }
                      label={p}
                    />
                  );
                }

                const isSftpCredential =
                  moduleMeta.id === SFTP_PROVIDER_ID && p === "password";

                if (isSftpCredential) {
                  const encrypted = isEncryptedPuttyKey(params[p] ?? "");
                  return (
                    <Stack key={p} spacing={2}>
                      <TextField
                        required={!storedSecrets.includes(p)}
                        fullWidth
                        label={t("wizard.sftpCredential")}
                        multiline
                        minRows={3}
                        maxRows={10}
                        value={params[p] ?? ""}
                        onChange={(event) =>
                          onParamChange(p, event.target.value)
                        }
                        placeholder={t(storedSecrets.includes(p) ? "moduleEdit.secretUnchanged" : "wizard.sftpCredentialPlaceholder")}
                        helperText={t(storedSecrets.includes(p) ? "moduleEdit.secretUnchanged" : "wizard.sftpCredentialHelp")}
                        disabled={disabled}
                      />
                      {encrypted && (
                        <TextField
                          required
                          fullWidth
                          label={t("wizard.sftpKeyPassphrase")}
                          type="password"
                          value={privateKeyPassphrase}
                          onChange={(event) =>
                            onPrivateKeyPassphraseChange(event.target.value)
                          }
                          helperText={t("wizard.sftpKeyPassphraseHelp")}
                          autoComplete="new-password"
                          disabled={disabled}
                        />
                      )}
                    </Stack>
                  );
                }

                const isPassword = p.toLowerCase().includes("password") || p === "secretKey" || p === "accessKey" || p === "token";
                const inputType =
                  isPassword && !showPassword[p] ? "password" : "text";

                return (
                  <TextField
                    key={p}
                    required={p !== "path" && !storedSecrets.includes(p)}
                    fullWidth
                    label={p === "playlistUrl" || p === "token" ? t(`wizard.${p}`) : p}
                    type={inputType}
                    value={params[p] ?? ""}
                    onChange={(e) => onParamChange(p, e.target.value)}
                    onPaste={onParamsPaste}
                    placeholder={storedSecrets.includes(p) ? t("moduleEdit.secretUnchanged") : t("wizard.enterValue", { param: p })}
                    helperText={storedSecrets.includes(p) ? t("moduleEdit.secretUnchanged") : undefined}
                    disabled={disabled}
                    InputProps={
                      isPassword
                        ? {
                            endAdornment: (
                              <InputAdornment position="end">
                                <IconButton
                                  aria-label={t(showPassword[p] ? "moduleEdit.hideSecret" : "moduleEdit.showSecret", { name: p })}
                                  onClick={() => togglePasswordVisibility(p)}
                                  edge="end"
                                  disabled={disabled}
                                >
                                  {showPassword[p] ? (
                                    <VisibilityOff />
                                  ) : (
                                    <Visibility />
                                  )}
                                </IconButton>
                              </InputAdornment>
                            ),
                          }
                        : undefined
                    }
                  />
                );
              })}
            </>
          )}
        </Stack>
      </CardContent>
    </Card>
  );
}
