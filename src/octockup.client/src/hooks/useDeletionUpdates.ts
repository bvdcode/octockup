import { useEffect } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../query/queryKeys";

export const deletionRefreshInterval = 5_000;

export function useDeletionUpdates(connection: HubConnection | null) {
  const queryClient = useQueryClient();
  useEffect(() => {
    if (!connection) {
      return;
    }
    const refresh = (backupId: string) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.backups });
      void queryClient.invalidateQueries({ queryKey: queryKeys.snapshots(backupId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.schedules });
    };
    connection.on("BackupDeletionChanged", refresh);
    return () => connection.off("BackupDeletionChanged", refresh);
  }, [connection, queryClient]);
}
