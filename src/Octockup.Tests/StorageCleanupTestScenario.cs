// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using EasyExtensions.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Enums;
using Octockup.Server.Modules;
using Octockup.Server.Services;
using System.Security.Cryptography;

namespace Octockup.Tests
{
    internal class StorageCleanupTestScenario : IAsyncDisposable
    {
        private const string TestMountsDirectoryName = "octockup-cleanup-tests";
        private readonly PostgresDbContext _dbContext;
        private readonly IStreamCipher _crypto;
        private readonly FileSystemBackupSource _provider;
        private readonly string _rootDirectory;
        private readonly Guid _storageId;
        private readonly Guid _backupId;
        private readonly Guid _cleanupId;
        private readonly Guid _runId;

        private StorageCleanupTestScenario(
            PostgresDbContext dbContext,
            IStreamCipher crypto,
            FileSystemBackupSource provider,
            string rootDirectory,
            Guid storageId,
            Guid backupId,
            Guid cleanupId,
            Guid runId)
        {
            _dbContext = dbContext;
            _crypto = crypto;
            _provider = provider;
            _rootDirectory = rootDirectory;
            _storageId = storageId;
            _backupId = backupId;
            _cleanupId = cleanupId;
            _runId = runId;
        }

        public PostgresDbContext DbContext => _dbContext;

        public static async Task<StorageCleanupTestScenario> CreateAsync(
            string connectionString,
            params IInterceptor[] interceptors)
        {
            string scenarioId = Guid.NewGuid().ToString("N");
            string mount = Path.Combine(TestMountsDirectoryName, scenarioId);
            string rootDirectory = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "mounts",
                mount));
            Directory.CreateDirectory(rootDirectory);

            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptors)
                .Options;
            PostgresDbContext dbContext = new(options);
            IStreamCipher crypto = new AesGcmStreamCipher(RandomNumberGenerator.GetBytes(32));
            FileSystemBackupSource provider = new(NullLogger<FileSystemBackupSource>.Instance);
            string providerId = typeof(FileSystemBackupSource).FullName!;
            User user = new()
            {
                Username = $"cleanup-{scenarioId}",
                PasswordPhc = "not-used",
            };
            Module source = new()
            {
                User = user,
                Tag = $"source-{scenarioId}",
                Destination = ModuleDestination.Source,
                BackupModuleId = providerId,
            };
            source.Params(crypto)["path"] = mount;
            Module storage = new()
            {
                User = user,
                Tag = $"storage-{scenarioId}",
                Destination = ModuleDestination.Target,
                BackupModuleId = providerId,
            };
            storage.Params(crypto)["path"] = mount;
            Backup backup = new()
            {
                Source = source,
                Storage = storage,
                Tag = $"backup-{scenarioId}",
                IgnoredPaths = [],
            };
            StorageCleanup cleanup = new()
            {
                Module = storage,
                Status = StorageCleanupStatus.Running,
                LastStartedAt = DateTime.UtcNow,
            };
            StorageCleanupRun run = new()
            {
                Module = storage,
                Status = StorageCleanupStatus.Running,
                StartedAt = cleanup.LastStartedAt.Value,
            };
            cleanup.LastRun = run;

            dbContext.AddRange(user, source, storage, backup, cleanup, run);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();
            return new StorageCleanupTestScenario(
                dbContext,
                crypto,
                provider,
                rootDirectory,
                storage.Id,
                backup.Id,
                cleanup.Id,
                run.Id);
        }

        public async Task AddUploadedChunkAsync(string hash, byte[] content)
        {
            await WriteChunkAsync(hash, content);
            await _dbContext.UploadedHashes.AddAsync(CreateUploadedHash(hash, content.LongLength));
            await SaveAndClearAsync();
        }

        public async Task AddUploadedHashesAsync(IReadOnlyCollection<string> hashes)
        {
            List<UploadedHash> uploadedHashes = hashes
                .Select(hash => CreateUploadedHash(hash, 1))
                .ToList();
            await _dbContext.UploadedHashes.AddRangeAsync(uploadedHashes);
            await SaveAndClearAsync();
        }

        public async Task AddQueuedChunkAsync(string hash, byte[] content)
        {
            await WriteChunkAsync(hash, content);
            await AddQueuedChunkAsync(hash, content.LongLength);
        }

        public async Task AddQueuedChunkAsync(string hash, long storedSize)
        {
            StorageCleanupChunk queued = new()
            {
                ModuleId = _storageId,
                Hash = hash,
                StoredSize = storedSize,
                OriginalSize = storedSize,
                CompressionAlgorithm = CompressionAlgorithm.None,
            };
            await _dbContext.StorageCleanupChunks.AddAsync(queued);
            await SaveAndClearAsync();
        }

        public async Task AddSnapshotFileAsync(IReadOnlyCollection<string> hashes)
        {
            Snapshot snapshot = new()
            {
                BackupId = _backupId,
                CompletedAt = DateTime.UtcNow,
            };
            SnapshotFile file = new()
            {
                Snapshot = snapshot,
                Path = "file.bin",
                Name = "file.bin",
                Hashsum = new string('f', 64),
                ChunkHashes = hashes.ToList(),
            };
            _dbContext.AddRange(snapshot, file);
            await SaveAndClearAsync();
        }

        public async Task ProcessAsync()
        {
            StorageCleanup cleanup = await _dbContext.StorageCleanups
                .Include(x => x.Module)
                .SingleAsync(x => x.Id == _cleanupId);
            StorageCleanupRun run = await _dbContext.StorageCleanupRuns
                .SingleAsync(x => x.Id == _runId);
            StorageCleanupProcessor processor = new(
                _crypto,
                _dbContext,
                [_provider],
                NullLogger<StorageCleanupProcessor>.Instance);
            await processor.ProcessAsync(cleanup, run, CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
        }

        public Task<StorageCleanup> GetCleanupAsync()
        {
            return _dbContext.StorageCleanups
                .AsNoTracking()
                .SingleAsync(x => x.Id == _cleanupId);
        }

        public async Task ExecuteJobAsync()
        {
            StorageCleanupProcessor processor = new(
                _crypto,
                _dbContext,
                [_provider],
                NullLogger<StorageCleanupProcessor>.Instance);
            StorageCleanupJob job = new(
                _dbContext,
                processor,
                new StorageOperationCoordinator(),
                NullLogger<StorageCleanupJob>.Instance);
            try
            {
                await job.ExecuteAsync(CancellationToken.None);
            }
            finally
            {
                _dbContext.ChangeTracker.Clear();
            }
        }

        public Task<StorageCleanupRun> GetRunAsync()
        {
            return _dbContext.StorageCleanupRuns
                .AsNoTracking()
                .SingleAsync(x => x.Id == _runId);
        }

        public Task<List<string>> UploadedHashesAsync()
        {
            return _dbContext.UploadedHashes
                .AsNoTracking()
                .Where(x => x.ModuleId == _storageId)
                .OrderBy(x => x.Hash)
                .Select(x => x.Hash)
                .ToListAsync();
        }

        public Task<List<string>> QueuedChunksAsync()
        {
            return _dbContext.StorageCleanupChunks
                .AsNoTracking()
                .Where(x => x.ModuleId == _storageId)
                .OrderBy(x => x.Hash)
                .Select(x => x.Hash)
                .ToListAsync();
        }

        public bool ChunkExists(string hash)
        {
            string relativePath = ChunkStorageHelpers.GetStoragePath(hash, Path.DirectorySeparatorChar);
            return File.Exists(Path.Combine(_rootDirectory, relativePath));
        }

        private UploadedHash CreateUploadedHash(string hash, long size)
        {
            return new UploadedHash
            {
                ModuleId = _storageId,
                Hash = hash,
                StoredSize = size,
                OriginalSize = size,
                CompressionAlgorithm = CompressionAlgorithm.None,
            };
        }

        private async Task WriteChunkAsync(string hash, byte[] content)
        {
            string relativePath = ChunkStorageHelpers.GetStoragePath(hash, Path.DirectorySeparatorChar);
            string path = Path.Combine(_rootDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content);
        }

        private async Task SaveAndClearAsync()
        {
            await _dbContext.SaveChangesAsync();
            _dbContext.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }
        }
    }
}
