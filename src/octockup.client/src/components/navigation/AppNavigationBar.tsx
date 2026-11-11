import {
  AppBar,
  Box,
  ButtonBase,
  Tab,
  Tabs,
  Toolbar,
  Tooltip,
  Typography,
} from "@mui/material";
import { isValidElement } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import type { ReactKitProps } from "@bvdcode/react-kit";
import ProfileUserMenu from "./ProfileUserMenu";
import { useTranslation } from "react-i18next";

export default function AppNavigationBar({
  appName,
  logoUrl,
  pages,
}: ReactKitProps) {
  const location = useLocation();
  const { t } = useTranslation();
  const navigate = useNavigate();
  const displayedPages = pages.filter(
    (page) => page.name !== undefined || isValidElement(page.icon),
  );
  const currentIndex = displayedPages.findIndex(
    (page) => page.route === location.pathname,
  );

  return (
    <AppBar position="static">
      <Toolbar
        sx={{
          gap: 2,
          display: "grid",
          gridTemplateColumns: "auto minmax(0, 1fr) auto",
        }}
      >
        <ButtonBase
          onClick={() => navigate("/")}
          sx={{ display: "flex", alignItems: "center", gap: 1 }}
        >
          {logoUrl && (
            <Box
              component="img"
              src={logoUrl}
              alt={appName}
              width={40}
              height={40}
            />
          )}
          <Typography
            variant="h6"
            color="inherit"
            sx={{ display: { xs: "none", md: "block" } }}
          >
            {appName}
          </Typography>
        </ButtonBase>

        <Box display="flex" justifyContent="center" minWidth={0}>
          <Tabs
            value={currentIndex === -1 ? false : currentIndex}
            textColor="inherit"
            indicatorColor="secondary"
            variant="scrollable"
            scrollButtons="auto"
            onChange={(_event, value: number) => {
              const page = displayedPages[value];
              if (page) {
                navigate(page.url ?? page.route);
              }
            }}
          >
            {displayedPages.map((page) => {
              const icon = isValidElement(page.icon) ? page.icon : undefined;
              const label = t(page.name ?? page.route);
              return (
                <Tab
                  key={page.route}
                  aria-label={label}
                  label={icon ? undefined : label}
                  icon={
                    icon ? (
                      <Tooltip title={label} arrow>
                        <Box component="span" display="inline-flex">
                          {icon}
                        </Box>
                      </Tooltip>
                    ) : undefined
                  }
                  sx={{ minWidth: 40, px: 1 }}
                />
              );
            })}
          </Tabs>
        </Box>

        <ProfileUserMenu />
      </Toolbar>
    </AppBar>
  );
}
