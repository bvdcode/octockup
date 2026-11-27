import type { Theme } from "@mui/material/styles";
import type { SnackbarProviderProps } from "notistack";

export const snackbarOptions = {
  maxSnack: 1,
  preventDuplicate: true,
  anchorOrigin: { vertical: "bottom", horizontal: "left" },
  autoHideDuration: 6000,
} satisfies SnackbarProviderProps;

export const snackbarStyles = (theme: Theme) => ({
  "body .notistack-SnackbarContainer": {
    [theme.breakpoints.up("sm")]: { maxWidth: theme.spacing(60) },
    [theme.breakpoints.down("sm")]: { bottom: theme.spacing(10) },
  },
  ".notistack-MuiContent": { overflowWrap: "anywhere" },
  ".notistack-MuiContent.notistack-MuiContent-success": {
    backgroundColor: theme.palette.success.dark,
    color: theme.palette.getContrastText(theme.palette.success.dark),
  },
  ".notistack-MuiContent.notistack-MuiContent-error": {
    backgroundColor: theme.palette.error.dark,
    color: theme.palette.getContrastText(theme.palette.error.dark),
  },
});
