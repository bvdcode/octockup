import {
  Home,
  Backup,
  Schedule,
  CloudDone,
  CloudDownload,
  CleaningServices,
  GitHub,
  Settings,
  FolderOpen,
} from "@mui/icons-material";
import { useEffect, useRef } from "react";
import * as locales from "./locales";
import HomePage from "./pages/Home";
import SourcesPage from "./pages/Sources";
import StoragesPage from "./pages/Storages";
import BackupsPage from "./pages/Backups";
import BackupWizard from "./pages/BackupWizard";
import SourceWizard from "./pages/SourceWizard";
import StorageWizard from "./pages/StorageWizard";
import {
  AppShell,
  type ReactKitProps,
  type TokenPair,
  useAuthStore,
} from "@bvdcode/react-kit";
import SchedulesPage from "./pages/Schedules";
import ScheduleWizard from "./pages/ScheduleWizard";
import SnapshotsPage from "./pages/Snapshots";
import SnapshotFilesPage from "./pages/SnapshotFiles";
import SettingsPage from "./pages/Settings";
import StorageCleanupPage from "./pages/StorageCleanup";
import ProfilePage from "./pages/Profile";
import StorageBrowserPage from "./pages/StorageBrowser";
import { Fab } from "@mui/material";
import LoginPage from "./pages/Login";
import type { CurrentUser } from "./types/auth";
import { loginWithPassword } from "./api/passwordLoginApi";
import { toCookieSession } from "./utils/authSession";
import { queryClient } from "./query/queryClient";
import AppNavigationBar from "./components/navigation/AppNavigationBar";

const renderNavigationBar = (props: ReactKitProps) => (
  <AppNavigationBar {...props} />
);

function App() {
  const accessToken = useAuthStore((state) => state.accessToken);
  const previousAccessToken = useRef(accessToken);

  useEffect(() => {
    if (previousAccessToken.current && !accessToken) {
      queryClient.clear();
    }
    previousAccessToken.current = accessToken;
  }, [accessToken]);

  return (
    <>
      <AppShell
        appName="Octockup"
        logoUrl="/octockup.png"
        contentMaxWidth={800}
        translations={{
          en: { translation: locales.en },
          ru: { translation: locales.ru },
        }}
        renderLoginPage={() => <LoginPage />}
        renderNavigationBar={renderNavigationBar}
        authConfig={{
          usernamePattern: /^[a-zA-Z0-9._-]+$/,
          login: loginWithPassword,
          getUserInfo: async (axiosInstance) => {
            const response = await axiosInstance.get<CurrentUser>(
              "/api/v1/auth/me",
            );
            response.data.avatarUrl = "/octockup.png";
            return response.data;
          },
          refreshToken: async (_refreshToken, axiosInstance) => {
            const response = await axiosInstance.post<TokenPair>(
              "/api/v1/auth/refresh",
              { refreshToken: "" },
            );
            return toCookieSession(response.data);
          },
          logout: async (_refreshToken, axiosInstance) => {
            await axiosInstance.post("/api/v1/auth/logout");
          },
        }}
        pages={[
          {
            icon: <Home />,
            name: "Home",
            route: "/",
            component: <HomePage />,
          },
          {
            icon: <Backup />,
            name: "Sources",
            route: "/sources",
            component: <SourcesPage />,
          },
          {
            route: "/sources/new",
            component: <SourceWizard />,
          },
          {
            route: "/storages/new",
            component: <StorageWizard />,
          },
          {
            icon: <CloudDone />,
            name: "Backups",
            route: "/backups",
            component: <BackupsPage />,
          },
          {
            icon: <CloudDownload />,
            name: "Storages",
            route: "/storages",
            component: <StoragesPage />,
          },
          {
            route: "/backups/new",
            component: <BackupWizard />,
          },
          {
            route: "/backups/:backupId/snapshots",
            component: <SnapshotsPage />,
          },
          {
            route: "/backups/:backupId/snapshots/:snapshotId/files",
            component: <SnapshotFilesPage />,
          },
          {
            icon: <Schedule />,
            name: "Schedules",
            route: "/schedules",
            component: <SchedulesPage />,
          },
          {
            route: "/schedules/new",
            component: <ScheduleWizard />,
          },
          {
            icon: <CleaningServices />,
            name: "Storage Cleanup",
            route: "/admin/storage-cleanup",
            component: <StorageCleanupPage />,
          },
          {
            route: "/profile",
            component: <ProfilePage />,
          },
          {
            icon: <FolderOpen />,
            name: "storageBrowser.title",
            route: "/storage-browser",
            component: <StorageBrowserPage />,
          },
          {
            icon: <Settings />,
            name: "Settings",
            route: "/settings",
            component: <SettingsPage />,
          },
        ]}
      />
      <Fab
        color="primary"
        aria-label="add"
        sx={{ position: "fixed", bottom: 16, right: 16 }}
        href="https://github.com/bvdcode/octockup"
        target="_blank"
        rel="noopener noreferrer"
      >
        <GitHub />
      </Fab>
    </>
  );
}

export default App;
