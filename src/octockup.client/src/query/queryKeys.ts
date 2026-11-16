export const queryKeys = {
  backups: ["backups"],
  modules: ["modules"],
  moduleParameters: (moduleId: string) => ["module-parameters", moduleId],
  moduleProviders: (type: "source" | "storage") => [
    "module-providers",
    type,
  ],
  schedules: ["schedules"],
  snapshots: (backupId: string) => ["snapshots", backupId],
  snapshotFiles: (snapshotId: string) => ["snapshot-files", snapshotId],
  currentUser: ["current-user"],
  authenticationOptions: ["authentication-options"],
  externalIdentities: ["external-identities"],
  authenticationSettings: ["authentication-settings"],
  oidcProviders: ["oidc-providers"],
  users: ["users"],
  storageCleanup: ["storage-cleanup"],
} as const;
