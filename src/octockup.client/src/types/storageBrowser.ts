export interface BrowserSnapshot {
  id: string; backupId: string; generation: string; tag: string;
  createdAt: string; completedAt: string; filesCount: number; totalSize: number;
}
export interface BrowserEntry {
  path: string; name: string; isDirectory: boolean; size: number; lastModified: string | null;
}
export interface BrowserPage { entries: BrowserEntry[]; page: number; pages: number; total: number }
export interface BrowserSession { session: string; expiresAt: string; snapshots: BrowserSnapshot[] }
export interface BrowserDiscovery {
  found: boolean; canPublish: boolean;
  publication: { status: "idle" | "queued" | "running" | "completed" | "failed"; completed: number; total: number };
}
