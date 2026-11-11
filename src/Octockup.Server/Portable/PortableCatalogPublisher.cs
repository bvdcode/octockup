// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Services;
namespace Octockup.Server.Portable
{
    public class PortableCatalogPublisher(AppDbContext db, IStreamCipher cipher, PortableStorageFactory factory,
        PortablePublicationStore publications, StorageOperationCoordinator operations)
    {
        public async Task PublishAsync(Guid userId, Guid moduleId, CancellationToken ct, bool customKeysOnly = false)
        {
            IBackupStorage storage = await factory.OpenAsync(userId, moduleId, ct);
            SemaphoreSlim gate = publications.GetLock(moduleId);
            List<Guid> backups = await db.Backups.AsNoTracking().Where(x => x.StorageId == moduleId &&
                x.Source.UserId == userId && x.DeletionRequestedAt == null &&
                x.Snapshots.Any(s => s.CompletedAt != null && s.DeletionRequestedAt == null) &&
                (!customKeysOnly || x.EncryptedMasterKey != null)).Select(x => x.Id).ToListAsync(ct);
            int completed = 0;
            publications.Set(userId, moduleId, new("running", 0, backups.Count));
            foreach (Guid backupId in backups)
            {
                await using StorageOperationLease lease = await operations.AcquireSnapshotUseAsync(backupId, ct);
                await gate.WaitAsync(ct);
                try
                {
                    Backup backup = await db.Backups.AsNoTracking().SingleAsync(x => x.Id == backupId, ct);
                    IStreamCipher backupCipher = BackupKeyCipher.Resolve(backup, cipher);
                    PortableCatalog catalog = await ReadCatalogAsync(storage, backupCipher, backupId, ct);
                    List<Snapshot> snapshots = await db.Snapshots.AsNoTracking().Include(x => x.Backup)
                        .Where(x => x.BackupId == backupId && x.Backup.DeletionRequestedAt == null &&
                            x.DeletionRequestedAt == null && x.CompletedAt != null).OrderBy(x => x.CreatedAt).ToListAsync(ct);
                    foreach (Snapshot snapshot in snapshots)
                    {
                        if (catalog.Snapshots.Any(x => x.Id == snapshot.Id))
                        {
                            continue;
                        }
                        PortableSnapshot published = await PublishSnapshotAsync(storage, backupCipher, snapshot, ct);
                        catalog.Snapshots.Add(published);
                        await PortableMetadata.WriteAsync(storage, backupCipher, PortableFormat.BackupCatalog(backupId), catalog, ct);
                    }
                    PortableIndex index = await storage.ExistsAsync(PortableFormat.CatalogPath, ct) == true
                        ? await PortableMetadata.ReadIndexAsync(storage, ct) : new(PortableFormat.Version, []);
                    if (catalog.Snapshots.Count > 0 && !index.Backups.Contains(backupId))
                    {
                        index.Backups.Add(backupId);
                        await PortableMetadata.WriteIndexAsync(storage, index, ct);
                    }
                    publications.Set(userId, moduleId, new("running", ++completed, backups.Count));
                }
                finally
                {
                    gate.Release();
                }
            }
            publications.Set(userId, moduleId, new("completed", completed, backups.Count));
        }

        public async Task RemoveAsync(Guid moduleId, Guid snapshotId, CancellationToken ct)
        {
            Module? module = await db.Modules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == moduleId, ct);
            if (module is null)
            {
                return;
            }
            IBackupStorage storage = await factory.OpenAsync(module.UserId, moduleId, ct);
            if (await storage.ExistsAsync(PortableFormat.CatalogPath, ct) != true)
            {
                return;
            }
            SemaphoreSlim gate = publications.GetLock(moduleId);
            await gate.WaitAsync(ct);
            try
            {
                Backup backup = await db.Snapshots.Where(x => x.Id == snapshotId).Select(x => x.Backup).SingleAsync(ct);
                IStreamCipher backupCipher = BackupKeyCipher.Resolve(backup, cipher);
                PortableCatalog catalog = await ReadCatalogAsync(storage, backupCipher, backup.Id, ct);
                PortableSnapshot? snapshot = catalog.Snapshots.SingleOrDefault(x => x.Id == snapshotId);
                if (snapshot is not null)
                {
                    await DeleteDirectoryAsync(storage, backupCipher, snapshot.Generation, string.Empty, ct);
                    catalog.Snapshots.Remove(snapshot);
                }
                if (catalog.Snapshots.Count > 0)
                {
                    await PortableMetadata.WriteAsync(storage, backupCipher, PortableFormat.BackupCatalog(backup.Id), catalog, ct);
                }
                else
                {
                    PortableIndex index = await PortableMetadata.ReadIndexAsync(storage, ct);
                    index.Backups.Remove(backup.Id);
                    if (index.Backups.Count > 0)
                    {
                        await PortableMetadata.WriteIndexAsync(storage, index, ct);
                    }
                    else
                    {
                        await DeleteObjectAsync(storage, PortableFormat.CatalogPath, ct);
                    }
                    await DeleteObjectAsync(storage, PortableFormat.BackupCatalog(backup.Id), ct);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<PortableCatalog> ReadCatalogAsync(IBackupStorage storage, IStreamCipher backupCipher,
            Guid backupId, CancellationToken ct)
        {
            if (await storage.ExistsAsync(PortableFormat.BackupCatalog(backupId), ct) != true)
            {
                return new(PortableFormat.Version, []);
            }
            PortableCatalog catalog = await PortableMetadata.ReadAsync<PortableCatalog>(storage, backupCipher, PortableFormat.BackupCatalog(backupId), ct);
            if (catalog.Version != PortableFormat.Version)
            {
                throw new InvalidDataException("Unsupported catalog version.");
            }
            return catalog;
        }

        private async Task<PortableSnapshot> PublishSnapshotAsync(IBackupStorage storage, IStreamCipher backupCipher,
            Snapshot snapshot, CancellationToken ct)
        {
            Guid generation = snapshot.Id;
            await using PortableTreeSpool tree = new();
            await tree.EnsureDirectoryAsync(string.Empty, ct);
            string? cursor = null;
            while (true)
            {
                IQueryable<SnapshotFile> query = db.SnapshotFiles.AsNoTracking().Where(x => x.SnapshotId == snapshot.Id);
                if (cursor is not null)
                {
                    query = query.Where(x => x.Path.CompareTo(cursor) > 0);
                }
                List<SnapshotFile> batch = await query.OrderBy(x => x.Path).Take(PortableFormat.PageSize).ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }
                Dictionary<string, UploadedHash> chunks = await LoadChunksAsync(snapshot.Backup.StorageId, batch, ct);
                foreach (SnapshotFile file in batch)
                {
                    List<ChunkStorageDescriptor> descriptors = file.ChunkHashes.Select(key =>
                    {
                        if (!chunks.TryGetValue(key, out UploadedHash? chunk))
                        {
                            throw new InvalidDataException("Chunk metadata is missing. Catalog publication was stopped.");
                        }
                        return ChunkStorageHelpers.Parse(key, chunk.CompressionAlgorithm, chunk.OriginalSize);
                    }).ToList();
                    string path = PortableFormat.Normalize(file.Path);
                    if (path.Length == 0)
                    {
                        path = PortableFormat.Normalize(file.Name);
                    }
                    await tree.AddAsync(new(path, path[(path.LastIndexOf('/') + 1)..], false,
                        file.Size, file.LastModified, file.Hashsum, descriptors), ct);
                }
                cursor = batch[^1].Path;
            }
            string? directory = null;
            long count = 0;
            int pages = 0;
            await foreach ((string Path, List<PortableEntry> Entries, int Page) part in tree.ReadPagesAsync(ct))
            {
                if (directory is not null && directory != part.Path)
                {
                    await SaveDirectoryAsync(storage, backupCipher, generation, directory, pages, count, ct);
                    count = 0;
                }
                directory = part.Path;
                pages = part.Page + 1;
                count += part.Entries.Count;
                await PortableMetadata.WriteAsync(storage, backupCipher,
                    $"{PortableFormat.DirectoryPath(generation, directory)}/{part.Page}", part.Entries, ct);
            }
            if (directory is not null)
            {
                await SaveDirectoryAsync(storage, backupCipher, generation, directory, pages, count, ct);
            }
            return new(snapshot.Id, snapshot.BackupId, generation, snapshot.Backup.Tag, snapshot.CreatedAt,
                snapshot.CompletedAt!.Value, snapshot.FilesCount, snapshot.TotalSize);
        }
        private static Task SaveDirectoryAsync(IBackupStorage storage, IStreamCipher backupCipher, Guid generation,
            string path, int pages, long count, CancellationToken ct) =>
            PortableMetadata.WriteAsync(storage, backupCipher, PortableFormat.DirectoryPath(generation, path) + "/directory",
                new PortableDirectory(pages, count), ct);

        private async Task<Dictionary<string, UploadedHash>> LoadChunksAsync(Guid moduleId, List<SnapshotFile> files, CancellationToken ct)
        {
            Dictionary<string, UploadedHash> result = new(StringComparer.Ordinal);
            foreach (string[] keys in files.SelectMany(x => x.ChunkHashes).Distinct().Chunk(10_000))
            {
                List<UploadedHash> batch = await db.UploadedHashes.AsNoTracking()
                    .Where(x => x.ModuleId == moduleId && keys.Contains(x.Hash)).ToListAsync(ct);
                foreach (UploadedHash chunk in batch)
                {
                    result.Add(chunk.Hash, chunk);
                }
            }
            return result;
        }

        private static async Task DeleteDirectoryAsync(IBackupStorage storage, IStreamCipher cipher,
            Guid generation, string path, CancellationToken ct)
        {
            string prefix = PortableFormat.DirectoryPath(generation, path);
            string header = prefix + "/directory";
            if (await storage.ExistsAsync(header, ct) != true)
            {
                return;
            }
            PortableDirectory directory = await PortableMetadata.ReadAsync<PortableDirectory>(storage, cipher, header, ct);
            for (int page = 0; page < directory.Pages; page++)
            {
                string pagePath = $"{prefix}/{page}";
                if (await storage.ExistsAsync(pagePath, ct) != true)
                {
                    continue;
                }
                List<PortableEntry> entries = await PortableMetadata.ReadAsync<List<PortableEntry>>(storage, cipher, pagePath, ct);
                foreach (PortableEntry entry in entries.Where(x => x.IsDirectory))
                {
                    if (entry.Name.Length == 0 || entry.Name.Contains('/') || entry.Name.Contains('\\') ||
                        PortableFormat.Normalize(entry.Path) != entry.Path || entry.Path.Count(x => x == '/') > 256 ||
                        entry.Path != (path.Length == 0 ? entry.Name : path + "/" + entry.Name))
                    {
                        throw new InvalidDataException("Invalid catalog directory.");
                    }
                    await DeleteDirectoryAsync(storage, cipher, generation, entry.Path, ct);
                }
                await DeleteObjectAsync(storage, pagePath, ct);
            }
            await DeleteObjectAsync(storage, header, ct);
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
