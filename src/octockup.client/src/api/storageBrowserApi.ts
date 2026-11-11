import { useMemo } from "react";
import { useAxios } from "@bvdcode/react-kit";
import type { AxiosInstance } from "axios";
import type { BrowserDiscovery, BrowserPage, BrowserSession } from "../types/storageBrowser";
class StorageBrowserApi {
  constructor(private readonly axios: AxiosInstance) {}
  async discover(module: string): Promise<BrowserDiscovery> {
    return (await this.axios.get<BrowserDiscovery>(`/api/v1/storage-browser/${module}`)).data;
  }
  async publish(module: string): Promise<void> {
    await this.axios.post(`/api/v1/storage-browser/${module}/publish`);
  }
  async unlock(module: string, masterKey: string): Promise<BrowserSession> {
    return (await this.axios.post<BrowserSession>(`/api/v1/storage-browser/${module}/unlock`, { masterKey: masterKey.trim() })).data;
  }
  async close(session: string): Promise<void> {
    await this.axios.delete("/api/v1/storage-browser/session", { headers: { "X-Storage-Session": session } });
  }
  async directory(session: string, snapshotId: string, path: string, page: number): Promise<BrowserPage> {
    return (await this.axios.get<BrowserPage>("/api/v1/storage-browser/directory", {
      headers: { "X-Storage-Session": session }, params: { snapshotId, path, page },
    })).data;
  }
}
export function useStorageBrowserApi() {
  const axios = useAxios();
  return useMemo(() => new StorageBrowserApi(axios), [axios]);
}
