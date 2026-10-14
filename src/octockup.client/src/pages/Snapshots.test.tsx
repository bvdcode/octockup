import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  createTestQueryClient,
  renderWithQueryClient,
} from "../test/renderWithQueryClient";
import type { SnapshotDto } from "../types/api";
import SnapshotsPage from "./Snapshots";

interface DeferredRequest {
  promise: Promise<void>;
  resolve: () => void;
}

interface TestGridColumn {
  field: string;
  renderCell?: (params: { row: SnapshotDto }) => ReactNode;
}

interface TestDataGridProps {
  columns: TestGridColumn[];
  rows: SnapshotDto[];
}

const api = vi.hoisted(() => ({
  deleteSnapshot: vi.fn(),
  listByBackup: vi.fn(),
}));
const router = vi.hoisted(() => ({
  navigate: vi.fn(),
}));

vi.mock("../api/snapshotsApi", () => ({
  useSnapshotsApi: () => api,
}));

vi.mock("@mui/x-data-grid", () => ({
  DataGrid: ({ columns, rows }: TestDataGridProps) => {
    const actionsColumn = columns.find((column) => column.field === "actions");
    return (
      <div>
        {rows.map((row) => (
          <div key={row.id}>{actionsColumn?.renderCell?.({ row })}</div>
        ))}
      </div>
    );
  },
}));

vi.mock("@bvdcode/react-kit", () => ({
  useAuthStore: <T,>(selector: (state: { accessToken: string }) => T): T =>
    selector({ accessToken: "access-token" }),
}));

vi.mock("material-ui-confirm", () => ({
  confirm: vi.fn().mockResolvedValue({ confirmed: true }),
}));

vi.mock("react-i18next", () => ({
  useTranslation: () => ({
    t: (key: string) => key,
  }),
}));

vi.mock("react-router-dom", () => ({
  useNavigate: () => router.navigate,
  useParams: () => ({ backupId: "backup-id" }),
}));

function createDeferredRequest(): DeferredRequest {
  let resolve: () => void = () => {};
  const promise = new Promise<void>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

describe("SnapshotsPage", () => {
  afterEach(cleanup);

  beforeEach(() => {
    vi.clearAllMocks();
    api.listByBackup.mockResolvedValue([
      {
        id: "first-snapshot",
        backupId: "backup-id",
        completedAt: "2026-08-09T00:00:00Z",
        filesCount: 10,
        totalSize: 100,
      },
      {
        id: "second-snapshot",
        backupId: "backup-id",
        completedAt: "2026-08-08T00:00:00Z",
        filesCount: 20,
        totalSize: 200,
      },
    ]);
  });

  it("tracks concurrent snapshot deletions independently", async () => {
    const firstRequest = createDeferredRequest();
    const secondRequest = createDeferredRequest();
    api.deleteSnapshot.mockImplementation((snapshotId: string) => {
      switch (snapshotId) {
        case "first-snapshot":
          return firstRequest.promise;
        case "second-snapshot":
          return secondRequest.promise;
        default:
          throw new Error(`Unexpected snapshot: ${snapshotId}`);
      }
    });
    const user = userEvent.setup();

    renderWithQueryClient(<SnapshotsPage />, createTestQueryClient());

    const deleteButtons = await screen.findAllByRole("button", {
      name: "snapshots.deleteTooltip",
    });
    await user.click(deleteButtons[0]);
    await user.click(deleteButtons[1]);

    await waitFor(() => {
      expect(api.deleteSnapshot).toHaveBeenCalledTimes(2);
      expect(deleteButtons[0]).toBeDisabled();
      expect(deleteButtons[1]).toBeDisabled();
    });

    firstRequest.resolve();

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: "snapshots.deleteTooltip" }),
      ).toBeDisabled();
    });

    secondRequest.resolve();

    await waitFor(() => {
      expect(
        screen.queryByRole("button", { name: "snapshots.deleteTooltip" }),
      ).not.toBeInTheDocument();
    });
  });

  it("starts a direct browser download without opening a new window", async () => {
    let clickedHref = "";
    let clickedDownload = "missing";
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(function (this: HTMLAnchorElement) {
        clickedHref = this.href;
        clickedDownload = this.download;
      });
    const user = userEvent.setup();

    renderWithQueryClient(<SnapshotsPage />, createTestQueryClient());

    const options = await screen.findAllByRole("button", {
      name: "snapshots.downloadOptions",
    });
    await user.click(options[0]);
    await user.click(
      screen.getByRole("button", { name: "snapshots.download" }),
    );

    expect(click).toHaveBeenCalledOnce();
    expect(clickedHref).toBe(
      "http://localhost:3000/api/v1/snapshots/first-snapshot/download?access_token=access-token&validate=false",
    );
    expect(clickedDownload).toBe("");
    expect(screen.getByRole("alert")).toHaveTextContent(
      "snapshots.downloadRequested",
    );
    click.mockRestore();
  });
});
