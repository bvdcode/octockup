export enum ModuleDestination {
  Source = 1,
  Target = 2,
}

// Match backend JSON (C# property names are PascalCase)
export interface Module {
  id: string;
  createdAt: string;
  userId: string;
  tag: string;
  backupModuleId: string;
  destination: ModuleDestination;
}

export interface ModuleProviderInfo {
  id: string;
  pathSeparator: string;
  name: string;
  requiredParameters: string[];
}

export type BackupSource = ModuleProviderInfo;
export type BackupStorage = ModuleProviderInfo;

export interface TestResultItem {
  path: string;
  name: string;
  size: number;
  lastModified?: string;
}

export interface CreateModuleRequest {
  destination: ModuleDestination;
  tag: string;
  backupModuleId: string;
  parameters: Record<string, string>;
}
export interface BackupItem {
  deletionRequestedAt?: string | null;
  deletionError?: string | null;
  id: string;
  tag: string;
  sourceId: string;
  storageId: string;
  ignoredPaths: string[];
  disableCompression: boolean;
  disableEncryption: boolean;
  source: Module;
  storage: Module;
  schedules: ScheduleItem[];
  snapshots: SnapshotDto[];
  createdAt?: string;
  updatedAt?: string;
}

export interface ScheduleBackupItem {
  id: string;
  tag: string;
  sourceId: string;
  storageId: string;
  ignoredPaths: string[];
  disableCompression: boolean;
  disableEncryption: boolean;
  source: Module;
  storage: Module;
  snapshots: SnapshotDto[];
  createdAt?: string;
  updatedAt?: string;
}

export interface SnapshotDto {
  deletionRequestedAt?: string | null;
  deletionError?: string | null;
  id: string;
  backupId: string;
  completedAt?: string | null;
  filesCount: number;
  totalSize: number;
}

export interface CreateBackupRequest {
  sourceId: string;
  storageId: string;
  tag: string;
  ignoredPaths: string[];
  disableCompression: boolean;
  disableEncryption: boolean;
}

export enum BackupStatus {
  Created = 0,
  Running = 1,
  Failed = 2,
  Completed = 3,
}

export interface ScheduleItem {
  id: string;
  backupId: string;
  startAt: string;
  interval?: string | null;
  status: BackupStatus;
  finishedAt?: string | null;
  errorMessage?: string | null;
  backup: ScheduleBackupItem;
}

export interface CreateScheduleRequest {
  backupId: string;
  startAt: string;
  intervalMinutes?: number;
}

export interface ScheduleReport {
  scheduleId: string;
  backupId: string;
  timestamp: string;
  status: BackupStatus;
  message: string;
  total: number;
  processed: number;
  speed: number;
  elapsed?: string;
  isEnumerationCompleted: boolean;
  currentPath: string;
  currentFile: string;
}

export interface SnapshotFileDto {
  id: string;
  size: number;
  snapshotId: string;
  lastModified?: string | null;
  name: string;
  path: string;
  hashsum: string;
}
