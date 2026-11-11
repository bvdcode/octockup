import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import StorageBrowserPage from "./StorageBrowser";
import { renderWithQueryClient } from "../test/renderWithQueryClient";

const api = vi.hoisted(() => ({
  discover: vi.fn(), unlock: vi.fn(), close: vi.fn(), directory: vi.fn(), publish: vi.fn(),
}));
const modules = vi.hoisted(() => ({ list: vi.fn() }));
vi.mock("../api/storageBrowserApi", () => ({ useStorageBrowserApi: () => api }));
vi.mock("../api/modulesApi", () => ({ useModulesApi: () => modules }));
vi.mock("@bvdcode/react-kit", () => ({ useAuthStore: () => "access-token" }));
vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (key: string) => key, i18n: { resolvedLanguage: "en" } }) }));

async function selectTarget() {
  await waitFor(() => expect(modules.list).toHaveBeenCalled());
  fireEvent.mouseDown(screen.getByRole("combobox"));
  fireEvent.click(await screen.findByRole("option", { name: "Archive target" }));
}

describe("StorageBrowserPage", () => {
  afterEach(() => { cleanup(); vi.restoreAllMocks(); });
  beforeEach(() => {
    vi.clearAllMocks();
    modules.list.mockResolvedValue([{ id: "target", tag: "Archive target", destination: 2 }]);
    api.discover.mockResolvedValue({ found: true, canPublish: false, publication: { status: "idle", completed: 0, total: 0 } });
    api.close.mockResolvedValue(undefined);
    api.directory.mockResolvedValue({ entries: [], page: 0, pages: 1, total: 0 });
    api.unlock.mockResolvedValue({ session: "view-session", snapshots: [{ id: "snapshot", tag: "Documents", completedAt: "2026-11-11T00:00:00Z" }] });
  });

  it("opens a temporary session, clears the key, and closes access explicitly", async () => {
    renderWithQueryClient(<StorageBrowserPage />);
    await selectTarget();
    const input = await screen.findByLabelText("storageBrowser.masterKey");
    fireEvent.change(input, { target: { value: "backup-key" } });
    fireEvent.click(screen.getByRole("button", { name: "storageBrowser.unlock" }));
    await screen.findByText("storageBrowser.empty");
    expect(api.unlock).toHaveBeenCalledWith("target", "backup-key");
    expect(api.directory).toHaveBeenCalledWith("view-session", "snapshot", "", 0);
    expect(screen.queryByLabelText("storageBrowser.masterKey")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "storageBrowser.close" }));
    expect(await screen.findByLabelText("storageBrowser.masterKey")).toHaveValue("");
    expect(api.close).toHaveBeenCalledWith("view-session");
  });

  it("keeps incorrect keys on the locked screen and shows the failure", async () => {
    api.unlock.mockRejectedValue(new Error("Incorrect backup key"));
    renderWithQueryClient(<StorageBrowserPage />);
    await selectTarget();
    fireEvent.change(await screen.findByLabelText("storageBrowser.masterKey"), { target: { value: "wrong-key" } });
    fireEvent.click(screen.getByRole("button", { name: "storageBrowser.unlock" }));
    expect(await screen.findByText("Incorrect backup key")).toBeInTheDocument();
    expect(api.directory).not.toHaveBeenCalled();
  });

  it("does not request a master key when the target has no catalog", async () => {
    api.discover.mockResolvedValue({ found: false, canPublish: false, publication: { status: "idle", completed: 0, total: 0 } });
    renderWithQueryClient(<StorageBrowserPage />);
    await selectTarget();
    expect(await screen.findByText("storageBrowser.notFound")).toBeInTheDocument();
    expect(screen.queryByLabelText("storageBrowser.masterKey")).not.toBeInTheDocument();
    expect(api.unlock).not.toHaveBeenCalled();
  });

  it("starts a streamed download with the viewing session, not the master key", async () => {
    api.directory.mockResolvedValue({ entries: [{ name: "file.txt", path: "file.txt", isDirectory: false, size: 17 }], page: 0, pages: 1, total: 1 });
    let posted: Record<string, FormDataEntryValue> = {};
    const submit = vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(function (this: HTMLFormElement) {
      posted = Object.fromEntries(new FormData(this));
    });
    renderWithQueryClient(<StorageBrowserPage />);
    await selectTarget();
    fireEvent.change(await screen.findByLabelText("storageBrowser.masterKey"), { target: { value: "backup-key" } });
    fireEvent.click(screen.getByRole("button", { name: "storageBrowser.unlock" }));
    await screen.findByText("file.txt");
    fireEvent.click(screen.getByRole("button", { name: "storageBrowser.downloadItem" }));
    expect(submit).toHaveBeenCalledOnce();
    expect(posted).toEqual({ session: "view-session", snapshotId: "snapshot", path: "file.txt", page: "0", folder: "false" });
    expect(document.querySelector("form")).toBeNull();
  });
});
