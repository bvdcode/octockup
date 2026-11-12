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
    public partial class PortableCatalogPublisher(AppDbContext db, IStreamCipher cipher, PortableStorageFactory factory,
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
                        await DeleteObjectAsync(storage, PortableFormat.PendingGeneration(snapshot.Id), ct);
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
                    string path = GetFilePath(file.Path, file.Name);
                    await tree.AddAsync(new(path, path[(path.LastIndexOf('/') + 1)..], false,
                        file.Size, file.LastModified, file.Hashsum, descriptors), ct);
                }
                cursor = batch[^1].Path;
            }
            await PortableMetadata.WriteAsync(storage, backupCipher, PortableFormat.PendingGeneration(generation), true, ct);
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

        private static string GetFilePath(string path, string name)
        {
            string normalized = PortableFormat.Normalize(path);
            if (normalized.Length == 0)
            {
                normalized = PortableFormat.Normalize(name);
            }
            return normalized;
        }
    }
}
