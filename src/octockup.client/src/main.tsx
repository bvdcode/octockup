import App from "./App.tsx";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClientProvider } from "@tanstack/react-query";
import { GlobalStyles } from "@mui/material";
import { SnackbarProvider } from "notistack";
import { snackbarOptions, snackbarStyles } from "./notifications/snackbar";
import { queryClient } from "./query/queryClient";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <GlobalStyles styles={snackbarStyles} />
      <SnackbarProvider {...snackbarOptions}>
        <App />
      </SnackbarProvider>
    </QueryClientProvider>
  </StrictMode>,
);
