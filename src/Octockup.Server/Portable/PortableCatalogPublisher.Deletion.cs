// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;

namespace Octockup.Server.Portable
{
    public partial class PortableCatalogPublisher
    {
        public async Task RemoveAsync(Guid moduleId, Guid snapshotId, CancellationToken ct)
        {
            Module? module = await db.Modules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == moduleId, ct);
            if (module is null)
            {
                return;
            }
            IBackupStorage storage = await factory.OpenAsync(module.UserId, moduleId, ct);
            SemaphoreSlim gate = publications.GetLock(moduleId);
            await gate.WaitAsync(ct);
            try
            {
                Backup backup = await db.Snapshots.Where(x => x.Id == snapshotId).Select(x => x.Backup).SingleAsync(ct);
                string catalogPath = PortableFormat.BackupCatalog(backup.Id);
                bool hasCatalog = await storage.ExistsAsync(catalogPath, ct) == true;
                bool pending = await storage.ExistsAsync(PortableFormat.PendingGeneration(snapshotId), ct) == true;
                if (!hasCatalog && !pending)
                {
                    return;
                }
                IStreamCipher backupCipher = BackupKeyCipher.Resolve(backup, cipher);
                PortableCatalog catalog = await ReadCatalogAsync(storage, backupCipher, backup.Id, ct);
                PortableSnapshot? snapshot = catalog.Snapshots.SingleOrDefault(x => x.Id == snapshotId);
                if (snapshot is null && !pending)
                {
                    return;
                }
                await DeleteGenerationAsync(storage, snapshotId, ct);
                if (snapshot is not null)
                {
                    catalog.Snapshots.Remove(snapshot);
                    if (catalog.Snapshots.Count > 0)
                    {
                        await PortableMetadata.WriteAsync(storage, backupCipher, catalogPath, catalog, ct);
                    }
                    else
                    {
                        await RemoveBackupIndexAsync(storage, backup.Id, ct);
                        await DeleteObjectAsync(storage, catalogPath, ct);
                    }
                }
                await DeleteObjectAsync(storage, PortableFormat.PendingGeneration(snapshotId), ct);
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task RemoveBackupIndexAsync(IBackupStorage storage, Guid backupId, CancellationToken ct)
        {
            if (await storage.ExistsAsync(PortableFormat.CatalogPath, ct) != true)
            {
                return;
            }
            PortableIndex index = await PortableMetadata.ReadIndexAsync(storage, ct);
            if (!index.Backups.Remove(backupId))
            {
                return;
            }
            if (index.Backups.Count > 0)
            {
                await PortableMetadata.WriteIndexAsync(storage, index, ct);
            }
            else
            {
                await DeleteObjectAsync(storage, PortableFormat.CatalogPath, ct);
            }
        }

        private async Task DeleteGenerationAsync(IBackupStorage storage, Guid snapshotId, CancellationToken ct)
        {
            await using PortableTreeSpool tree = new();
            await tree.EnsureDirectoryAsync(string.Empty, ct);
            string? cursor = null;
            while (true)
            {
                IQueryable<SnapshotFile> query = db.SnapshotFiles.AsNoTracking().Where(x => x.SnapshotId == snapshotId);
                if (cursor is not null)
                {
                    query = query.Where(x => x.Path.CompareTo(cursor) > 0);
                }
                var batch = await query.OrderBy(x => x.Path).Select(x => new { x.Path, x.Name })
                    .Take(PortableFormat.PageSize).ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }
                foreach ((string path, string name) in batch.Select(x => (x.Path, x.Name)))
                {
                    string normalized = GetFilePath(path, name);
                    await tree.AddAsync(new(normalized, normalized[(normalized.LastIndexOf('/') + 1)..], false, 0, null, null, []), ct);
                }
                cursor = batch[^1].Path;
            }
            await foreach ((string Path, List<PortableEntry> Entries, int Page) part in tree.ReadPagesAsync(ct))
            {
                string prefix = PortableFormat.DirectoryPath(snapshotId, part.Path);
                await DeleteObjectAsync(storage, $"{prefix}/{part.Page}", ct);
                if (part.Page == 0)
                {
                    await DeleteObjectAsync(storage, prefix + "/directory", ct);
                }
            }
        }

        private static async Task DeleteObjectAsync(IBackupStorage storage, string path, CancellationToken ct)
        {
            if (await storage.DeleteAsync(path, ct) != true && await storage.ExistsAsync(path, ct) == true)
            {
                throw new IOException("Could not remove catalog object.");
            }
        }
    }
}
