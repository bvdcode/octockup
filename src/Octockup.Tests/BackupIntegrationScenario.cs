// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using EasyExtensions.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Hubs;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Enums;
using Octockup.Server.Modules;
using Octockup.Server.Portable;
using Octockup.Server.Services;
using Octockup.Server.Streams;
using System.Security.Cryptography;

namespace Octockup.Tests
{
    internal class BackupIntegrationScenario : IAsyncDisposable
    {
        private const string TestMountsDirectoryName = "octockup-integration-tests";
        private readonly PostgresDbContext _dbContext;
        private readonly ServiceProvider _serviceProvider;
        private readonly IStreamCipher _crypto;
        private Guid _backupId;
        private Guid _storageModuleId;
        private readonly string _rootDirectory;
        private readonly string _sourceDirectory;
        private readonly string _storageDirectory;
        private readonly string _sourceMount;
        private readonly string _storageMount;
        private bool _disposed;

        private BackupIntegrationScenario(
            PostgresDbContext dbContext,
            ServiceProvider serviceProvider,
            IStreamCipher crypto,
            string rootDirectory,
            string sourceDirectory,
            string storageDirectory,
            string sourceMount,
            string storageMount)
        {
            _dbContext = dbContext;
            _serviceProvider = serviceProvider;
            _crypto = crypto;
            _rootDirectory = rootDirectory;
            _sourceDirectory = sourceDirectory;
            _storageDirectory = storageDirectory;
            _sourceMount = sourceMount;
            _storageMount = storageMount;
        }

        public static async Task<BackupIntegrationScenario> CreateAsync(
            string connectionString,
            IInterceptor? interceptor = null,
            CancellationToken cancellationToken = default)
        {
            string scenarioId = Guid.NewGuid().ToString("N");
            string relativeRoot = Path.Combine(TestMountsDirectoryName, scenarioId);
            string sourceMount = Path.Combine(relativeRoot, "source");
            string storageMount = Path.Combine(relativeRoot, "storage");
            string rootDirectory = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "mounts",
                relativeRoot));
            string sourceDirectory = Path.Combine(rootDirectory, "source");
            string storageDirectory = Path.Combine(rootDirectory, "storage");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(storageDirectory);

            DbContextOptionsBuilder<PostgresDbContext> optionsBuilder = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(connectionString);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }
            DbContextOptions<PostgresDbContext> options = optionsBuilder.Options;
            PostgresDbContext dbContext = new(options);
            ServiceCollection services = new();
            services.AddLogging();
            services.AddSignalR();
            ServiceProvider serviceProvider = services.BuildServiceProvider();
            IStreamCipher crypto = new AesGcmStreamCipher(RandomNumberGenerator.GetBytes(32));
            BackupIntegrationScenario scenario = new(
                dbContext,
                serviceProvider,
                crypto,
                rootDirectory,
                sourceDirectory,
                storageDirectory,
                sourceMount,
                storageMount);

            try
            {
                await scenario.SeedAsync(scenarioId, cancellationToken);
                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();
                throw;
            }
        }

        private async Task SeedAsync(string scenarioId, CancellationToken cancellationToken)
        {
            string providerId = typeof(FileSystemBackupSource).FullName!;
            User user = new()
            {
                Username = $"integration-{scenarioId}",
                PasswordPhc = "not-used",
            };
            Module source = new()
            {
                User = user,
                Tag = $"source-{scenarioId}",
                Destination = ModuleDestination.Source,
                BackupModuleId = providerId,
            };
            source.Params(_crypto)["path"] = _sourceMount;
            Module storage = new()
            {
                User = user,
                Tag = $"storage-{scenarioId}",
                Destination = ModuleDestination.Target,
                BackupModuleId = providerId,
            };
            storage.Params(_crypto)["path"] = _storageMount;
            Backup backup = new()
            {
                Source = source,
                Storage = storage,
                Tag = $"backup-{scenarioId}",
                IgnoredPaths = [],
            };

            _dbContext.AddRange(user, source, storage, backup);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _backupId = backup.Id;
            _storageModuleId = storage.Id;
            _dbContext.ChangeTracker.Clear();
        }

        public async Task WriteSourceFileAsync(
            string relativePath,
            byte[] content,
            DateTime? lastModified = null,
            CancellationToken cancellationToken = default)
        {
            string path = GetSourcePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            if (lastModified.HasValue)
            {
                File.SetLastWriteTimeUtc(path, lastModified.Value);
            }
        }

        public void DeleteSourceFile(string relativePath)
        {
            File.Delete(GetSourcePath(relativePath));
        }

        public DateTime GetSourceLastWriteTimeUtc(string relativePath)
        {
            return File.GetLastWriteTimeUtc(GetSourcePath(relativePath));
        }

        public PostgresDbContext DbContext => _dbContext;

        public Guid BackupId => _backupId;

        public async Task<Schedule> RunBackupAsync(CancellationToken cancellationToken = default)
        {
            Schedule schedule = new()
            {
                BackupId = _backupId,
                StartAt = DateTime.UtcNow,
                Status = ScheduleStatus.Created,
            };
            await _dbContext.Schedules.AddAsync(schedule, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            Guid scheduleId = schedule.Id;
            _dbContext.ChangeTracker.Clear();

            Schedule trackedSchedule = await _dbContext.Schedules
                .Include(x => x.Backup)
                    .ThenInclude(x => x.Source)
                .Include(x => x.Backup)
                    .ThenInclude(x => x.Storage)
                .SingleAsync(x => x.Id == scheduleId, cancellationToken);
            FileSystemBackupSource provider = ActivatorUtilities.CreateInstance<FileSystemBackupSource>(_serviceProvider);
            BackupRunner runner = new(
                _crypto,
                _dbContext,
                _serviceProvider,
                _serviceProvider.GetRequiredService<ILogger<BackupRunner>>(),
                _serviceProvider.GetRequiredService<IHubContext<EventHub>>(),
                [provider],
                new StorageOperationCoordinator());
            await runner.RunAsync(trackedSchedule, cancellationToken);
            _dbContext.ChangeTracker.Clear();

            return await _dbContext.Schedules
                .AsNoTracking()
                .SingleAsync(x => x.Id == scheduleId, cancellationToken);
        }

        public Task<Snapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return _dbContext.Snapshots
                .AsNoTracking()
                .Where(x => x.BackupId == _backupId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstAsync(cancellationToken);
        }

        public Task<SnapshotFile> GetSnapshotFileAsync(
            Guid snapshotId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return _dbContext.SnapshotFiles
                .AsNoTracking()
                .SingleAsync(
                    x => x.SnapshotId == snapshotId && x.Path == relativePath,
                    cancellationToken);
        }

        public string GetStorageObjectPath(string chunkKey)
        {
            string relativePath = ChunkStorageHelpers.GetStoragePath(chunkKey, Path.DirectorySeparatorChar);
            return Path.Combine(_storageDirectory, relativePath);
        }

        public string StorageMount => _storageMount;
        public Guid StorageModuleId => _storageModuleId;
        public AppDbContext Database => _dbContext;
        public IStreamCipher Cipher => _crypto;
        public IServiceProvider Services => _serviceProvider;

        public async Task SetMasterKeyAsync(string masterKey)
        {
            Backup backup = await _dbContext.Backups.SingleAsync(x => x.Id == _backupId);
            backup.EncryptedMasterKey = Convert.ToBase64String(_crypto.EncryptString(masterKey));
            await _dbContext.SaveChangesAsync();
            _dbContext.ChangeTracker.Clear();
        }

        public async Task CreateBackupWithKeyAsync(string masterKey)
        {
            Backup previous = await _dbContext.Backups.AsNoTracking().SingleAsync(x => x.Id == _backupId);
            Backup backup = new()
            {
                SourceId = previous.SourceId,
                StorageId = previous.StorageId,
                Tag = "backup-" + Guid.NewGuid().ToString("N"),
                IgnoredPaths = [],
                EncryptedMasterKey = Convert.ToBase64String(_crypto.EncryptString(masterKey)),
            };
            _dbContext.Add(backup);
            await _dbContext.SaveChangesAsync();
            _backupId = backup.Id;
            _dbContext.ChangeTracker.Clear();
        }

        public async Task<byte[]> RestoreFileAsync(
            Guid snapshotId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            SnapshotFile snapshotFile = await GetSnapshotFileAsync(snapshotId, relativePath, cancellationToken);
            List<string> chunkKeys = snapshotFile.ChunkHashes.ToList();
            Dictionary<string, UploadedHash> uploadedHashes = await _dbContext.UploadedHashes
                .AsNoTracking()
                .Where(x => x.ModuleId == _storageModuleId && chunkKeys.Contains(x.Hash))
                .ToDictionaryAsync(x => x.Hash, cancellationToken);
            List<ChunkStorageDescriptor> chunks = chunkKeys
                .Select(key => uploadedHashes.TryGetValue(key, out UploadedHash? uploadedHash)
                    ? ChunkStorageHelpers.Parse(key, uploadedHash.CompressionAlgorithm, uploadedHash.OriginalSize)
                    : ChunkStorageHelpers.Parse(key))
                .ToList();

            FileSystemBackupSource storage = ActivatorUtilities.CreateInstance<FileSystemBackupSource>(_serviceProvider);
            storage.SetParameters(new Dictionary<string, string> { ["path"] = _storageMount });
            await using SnapshotConcatStream stream = new(
                _serviceProvider.GetRequiredService<ILogger<SnapshotConcatStream>>(),
                storage,
                chunks,
                snapshotFile,
                BackupKeyCipher.Resolve(await _dbContext.Snapshots.Where(x => x.Id == snapshotId)
                    .Select(x => x.Backup).SingleAsync(cancellationToken), _crypto),
                cancellationToken);
            await using MemoryStream restored = new();
            await stream.CopyToAsync(restored, cancellationToken);
            return restored.ToArray();
        }

        private string GetSourcePath(string relativePath)
        {
            string path = Path.GetFullPath(Path.Combine(_sourceDirectory, relativePath));
            string sourcePrefix = _sourceDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Source path escapes the test directory: {relativePath}.");
            }

            return path;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _dbContext.DisposeAsync();
            await _serviceProvider.DisposeAsync();

            string testMountsRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "mounts",
                TestMountsDirectoryName));
            string expectedPrefix = testMountsRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!_rootDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to delete unexpected test directory: {_rootDirectory}.");
            }

            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
    }
}
