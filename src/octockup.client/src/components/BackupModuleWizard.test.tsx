import { cleanup, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SnackbarProvider } from "notistack";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { snackbarOptions } from "../notifications/snackbar";
import { renderWithQueryClient } from "../test/renderWithQueryClient";
import { ModuleDestination } from "../types/api";
import BackupModuleWizard from "./BackupModuleWizard";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

describe("BackupModuleWizard", () => {
  afterEach(cleanup);

  it("keeps the creation notification visible after returning to the catalog", async () => {
    const provider = { id: "local", name: "Local", pathSeparator: "/", requiredParameters: [] };
    const apiClient = {
      listProviders: vi.fn().mockResolvedValue([provider]),
      listProvidersByType: vi.fn().mockResolvedValue([provider]),
      create: vi.fn().mockResolvedValue(undefined),
      test: vi.fn().mockResolvedValue(undefined),
      getDirectories: vi.fn().mockResolvedValue([]),
      list: vi.fn().mockResolvedValue([]),
    };
    const user = userEvent.setup();
    renderWithQueryClient(
      <SnackbarProvider {...snackbarOptions}>
        <MemoryRouter initialEntries={["/sources/new?type=local"]}>
          <Routes>
            <Route path="/sources" element={<h1>Source catalog</h1>} />
            <Route path="/sources/new" element={
              <BackupModuleWizard moduleType="source" apiClient={apiClient} backRoute="/sources" />
            } />
          </Routes>
        </MemoryRouter>
      </SnackbarProvider>,
    );

    await user.type(await screen.findByRole("textbox", { name: /wizard\.tag/ }), "Local documents");
    await user.click(screen.getByRole("button", { name: "wizard.testConnection" }));
    await screen.findByText("wizard.testSuccess");
    await user.click(screen.getByRole("button", { name: "sources.create" }));

    expect(await screen.findByRole("heading", { name: "Source catalog" })).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent("wizard.sourceCreatedSuccess");
    expect(apiClient.create).toHaveBeenCalledWith("local", ModuleDestination.Source, "Local documents", "local", {});
  });
});
