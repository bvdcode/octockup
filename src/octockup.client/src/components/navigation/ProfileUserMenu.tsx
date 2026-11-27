import {
  Avatar,
  Box,
  Divider,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Popover,
  Typography,
} from "@mui/material";
import {
  DarkMode,
  Language,
  LightMode,
  Logout,
  Person,
} from "@mui/icons-material";
import { useState, type MouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  useAuthStore,
  useThemeMode,
} from "@bvdcode/react-kit";
import { useAuthApi } from "../../api/authApi";
import { queryKeys } from "../../query/queryKeys";

export default function ProfileUserMenu() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const authApi = useAuthApi();
  const logout = useAuthStore((state) => state.logout);
  const { mode, toggleTheme } = useThemeMode();
  const currentUserQuery = useQuery({
    queryKey: queryKeys.currentUser,
    queryFn: () => authApi.getCurrentUser(),
  });
  const currentUser = currentUserQuery.data;
  const [anchorElement, setAnchorElement] = useState<HTMLElement | null>(null);

  const closeMenu = () => setAnchorElement(null);

  const openProfile = () => {
    closeMenu();
    navigate("/profile");
  };

  const changeLanguage = () => {
    const configuredLanguages = i18n.options.supportedLngs;
    const supportedLanguages = Array.isArray(configuredLanguages)
      ? configuredLanguages.filter((language) => language !== "cimode")
      : [];
    if (supportedLanguages.length === 0) {
      return;
    }

    const currentLanguage = i18n.resolvedLanguage ?? i18n.language;
    const currentIndex = Math.max(
      0,
      supportedLanguages.indexOf(currentLanguage),
    );
    const nextLanguage =
      supportedLanguages[(currentIndex + 1) % supportedLanguages.length];
    if (nextLanguage && nextLanguage !== currentLanguage) {
      void i18n.changeLanguage(nextLanguage);
    }
  };

  const handleLogout = async () => {
    closeMenu();
    await logout();
  };

  const displayName =
    currentUser?.displayName || currentUser?.username || t("navigation.user");

  return (
    <Box display="flex" justifyContent="flex-end">
      <IconButton
        aria-label={t("navigation.userMenu")}
        onClick={(event: MouseEvent<HTMLElement>) =>
          setAnchorElement(event.currentTarget)
        }
        sx={{ p: 0 }}
      >
        <Avatar
          src={currentUser?.avatarUrl ?? "/octockup.png"}
          alt={displayName}
          slotProps={{ img: { referrerPolicy: "no-referrer" } }}
        >
          {displayName.slice(0, 1).toUpperCase()}
        </Avatar>
      </IconButton>
      <Popover
        open={anchorElement !== null}
        anchorEl={anchorElement}
        onClose={closeMenu}
        anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
        transformOrigin={{ vertical: "top", horizontal: "right" }}
        slotProps={{ paper: { sx: { mt: 1 } } }}
      >
        <Box p={2} minWidth={250}>
          <Typography variant="subtitle1" fontWeight="bold">
            {displayName}
          </Typography>
          {currentUser?.username && (
            <Typography variant="body2" color="text.secondary">
              @{currentUser.username}
            </Typography>
          )}
        </Box>
        <Divider />
        <List dense>
          <ListItemButton onClick={openProfile}>
            <ListItemIcon>
              <Person />
            </ListItemIcon>
            <ListItemText primary={t("navigation.profile")} />
          </ListItemButton>
          <ListItemButton onClick={toggleTheme}>
            <ListItemIcon>
              {mode === "dark" ? <LightMode /> : <DarkMode />}
            </ListItemIcon>
            <ListItemText
              primary={t(
                mode === "dark"
                  ? "navigation.lightMode"
                  : "navigation.darkMode",
              )}
            />
          </ListItemButton>
          <ListItemButton onClick={changeLanguage}>
            <ListItemIcon>
              <Language />
            </ListItemIcon>
            <ListItemText primary={t("navigation.language")} />
          </ListItemButton>
          <Divider />
          <ListItemButton onClick={() => void handleLogout()}>
            <ListItemIcon>
              <Logout />
            </ListItemIcon>
            <ListItemText primary={t("navigation.logout")} />
          </ListItemButton>
        </List>
      </Popover>
    </Box>
  );
}
