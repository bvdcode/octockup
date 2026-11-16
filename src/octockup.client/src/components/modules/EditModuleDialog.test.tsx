import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithQueryClient } from "../../test/renderWithQueryClient";
import { ModuleDestination } from "../../types/api";
import EditModuleDialog from "./EditModuleDialog";

const api = vi.hoisted(() => ({
  getParameters: vi.fn(),
  updateParameters: vi.fn(),
  testParameters: vi.fn(),
}));
vi.mock("../../api/modulesApi", () => ({ useModulesApi: () => api }));
vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

const module = {
  id: "source-1", tag: "Documents", userId: "owner", createdAt: "2026-01-01",
  backupModuleId: "Octockup.Server.Modules.IMAPSource", destination: ModuleDestination.Source,
};
const provider = {
  id: module.backupModuleId, name: "IMAP", pathSeparator: "/",
  requiredParameters: ["host", "port", "username", "password", "path", "useSsl"],
};

function renderDialog(onClose = vi.fn()) {
  return renderWithQueryClient(<EditModuleDialog module={module} provider={provider} onClose={onClose} />);
}

describe("EditModuleDialog", () => {
  afterEach(cleanup);
  beforeEach(() => {
    vi.resetAllMocks();
    api.getParameters.mockResolvedValue({
      parameters: { host: "old.example.com", port: "993", username: "account", path: "", useSsl: "true" },
      storedSecrets: ["password"],
    });
    api.testParameters.mockResolvedValue(undefined);
    api.updateParameters.mockResolvedValue(undefined);
  });

  it("tests and saves only the changed address, leaving the stored password untouched", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    renderDialog(onClose);
    const host = await screen.findByLabelText(/^host/);
    expect(screen.getByLabelText(/^password/)).toHaveValue("");
    expect(screen.getByLabelText(/^password/)).not.toBeRequired();
    expect(screen.getByRole("button", { name: "common.save" })).toBeDisabled();
    fireEvent.change(host, { target: { value: " new.example.com " } });

    await user.click(screen.getByRole("button", { name: "wizard.testConnection" }));
    await screen.findByText("wizard.testSuccess");
    expect(api.testParameters).toHaveBeenCalledWith(module.id, { host: "new.example.com" });
    expect(api.updateParameters).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "common.save" }));

    await waitFor(() => expect(api.updateParameters).toHaveBeenCalledWith(module.id, { host: "new.example.com" }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("requires a new connection test after changing the password", async () => {
    const user = userEvent.setup();
    renderDialog();
    await screen.findByLabelText(/^host/);
    await user.click(screen.getByRole("button", { name: "wizard.testConnection" }));
    await screen.findByText("wizard.testSuccess");

    fireEvent.change(screen.getByLabelText(/^password/), { target: { value: "replacement-password" } });
    expect(screen.getByRole("button", { name: "common.save" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "wizard.testConnection" }));
    await screen.findByText("wizard.testSuccess");
    expect(api.testParameters).toHaveBeenLastCalledWith(module.id, { password: "replacement-password" });
    await user.click(screen.getByRole("button", { name: "common.save" }));
    await waitFor(() => expect(api.updateParameters).toHaveBeenCalledWith(module.id, { password: "replacement-password" }));
  });

  it("keeps failed connection tests from saving and displays the error", async () => {
    api.testParameters.mockRejectedValue(new Error("Connection refused"));
    const user = userEvent.setup();
    renderDialog();
    await screen.findByLabelText(/^host/);
    await user.click(screen.getByRole("button", { name: "wizard.testConnection" }));

    await screen.findByText("Connection refused");
    expect(screen.getByRole("button", { name: "common.save" })).toBeDisabled();
    expect(api.updateParameters).not.toHaveBeenCalled();
  });

  it("discards draft edits on cancel without updating the module", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    renderDialog(onClose);
    fireEvent.change(await screen.findByLabelText(/^host/), { target: { value: "draft.example.com" } });
    await user.click(screen.getByRole("button", { name: "common.cancel" }));
    expect(onClose).toHaveBeenCalledOnce();
    expect(api.updateParameters).not.toHaveBeenCalled();
  });
});
