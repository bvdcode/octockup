// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using EasyExtensions.Models.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models;
using Octockup.Server.Models.Enums;
using Quartz;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        private PostgresDbContext CreateDbContext()
        {
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .Options;
            return new PostgresDbContext(options);
        }

        private static IStreamCipher CreateCipher()
        {
            return new AesGcmStreamCipher(RandomNumberGenerator.GetBytes(32));
        }

        private static async Task ConfigureDownloadAsync(
            AppDbContext dbContext,
            OwnedGraph graph,
            byte[] storedContent,
            byte[] expectedContent)
        {
            string contentHash = CalculateHash(storedContent);
            string chunkKey = ChunkStorageHelpers.CreateKey(
                contentHash,
                CompressionAlgorithm.None,
                isEncrypted: false);
            SnapshotFile snapshotFile = graph.SnapshotFile!;
            snapshotFile.Size = storedContent.Length;
            snapshotFile.Hashsum = CalculateHash(expectedContent);
            snapshotFile.ChunkHashes = [chunkKey];
            UploadedHash uploadedHash = new()
            {
                ModuleId = graph.Storage.Id,
                Hash = chunkKey,
                StoredSize = storedContent.Length,
                OriginalSize = storedContent.Length,
                CompressionAlgorithm = CompressionAlgorithm.None,
            };
            dbContext.UploadedHashes.Add(uploadedHash);
            await dbContext.SaveChangesAsync();
        }

        private static string CalculateHash(byte[] content)
        {
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        }

        private static async Task<Module> SeedStandaloneModuleAsync(AppDbContext dbContext)
        {
            string suffix = Guid.NewGuid().ToString("N");
            User user = new()
            {
                Username = $"owner-{suffix}",
                PasswordPhc = "not-used",
            };
            Module module = new()
            {
                User = user,
                Tag = $"module-{suffix}",
                BackupModuleId = "test-storage",
                Destination = ModuleDestination.Target,
            };
            await dbContext.Modules.AddAsync(module);
            await dbContext.SaveChangesAsync();
            return module;
        }

        private static async Task<OwnedGraph> SeedOwnedGraphAsync(
            AppDbContext dbContext,
            bool includeSchedule = false,
            bool includeSnapshot = false)
        {
            string suffix = Guid.NewGuid().ToString("N");
            User user = new()
            {
                Username = $"owner-{suffix}",
                PasswordPhc = "not-used",
            };
            Module source = new()
            {
                User = user,
                Tag = $"source-{suffix}",
                BackupModuleId = "test-source",
                Destination = ModuleDestination.Source,
            };
            Module storage = new()
            {
                User = user,
                Tag = $"storage-{suffix}",
                BackupModuleId = "test-storage",
                Destination = ModuleDestination.Target,
            };
            Backup backup = new()
            {
                Source = source,
                Storage = storage,
                Tag = $"backup-{suffix}",
            };
            OwnedGraph graph = new()
            {
                Source = source,
                Storage = storage,
                Backup = backup,
            };
            dbContext.Backups.Add(backup);

            if (includeSchedule)
            {
                graph.Schedule = new Schedule
                {
                    Backup = backup,
                    StartAt = DateTime.UtcNow,
                    Status = ScheduleStatus.Running,
                };
                dbContext.Schedules.Add(graph.Schedule);
            }

            if (includeSnapshot)
            {
                graph.Snapshot = new Snapshot
                {
                    Backup = backup,
                    CompletedAt = DateTime.UtcNow,
                    FilesCount = 1,
                    TotalSize = 1,
                };
                graph.SnapshotFile = new SnapshotFile
                {
                    Snapshot = graph.Snapshot,
                    Name = "file.bin",
                    Path = "file.bin",
                    Size = 1,
                    Hashsum = "hash",
                    ChunkHashes = [],
                };
                dbContext.SnapshotFiles.Add(graph.SnapshotFile);
            }

            await dbContext.SaveChangesAsync();
            return graph;
        }

        private static TController AsUser<TController>(TController controller, Guid userId)
            where TController : ControllerBase
        {
            ClaimsIdentity identity = new(
                [new Claim("sub", userId.ToString("D"))],
                "Test");
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                },
            };
            return controller;
        }

        private static void AssertNotFound(IActionResult result)
        {
            Assert.That(result, Is.AssignableTo<IStatusCodeActionResult>());
            IStatusCodeActionResult statusCodeResult = (IStatusCodeActionResult)result;
            Assert.That(statusCodeResult.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        }

        private class OwnedGraph
        {
            public required Module Source { get; init; }
            public required Module Storage { get; init; }
            public required Backup Backup { get; init; }
            public Schedule? Schedule { get; set; }
            public Snapshot? Snapshot { get; set; }
            public SnapshotFile? SnapshotFile { get; set; }
        }

        private class TestStorage(
            string id,
            byte[]? content = null,
            bool failOnAccess = false) : IBackupStorage
        {
            public string Id => id;
            public string Name => id;
            public char PathSeparator => '/';
            public IEnumerable<string> RequiredParameters => [];
            public bool WasAccessed { get; private set; }
            public int ReadCount { get; private set; }

            public void SetParameters(IReadOnlyDictionary<string, string> parameters)
            {
                WasAccessed = true;
                if (failOnAccess)
                {
                    throw new InvalidOperationException("Storage must not be accessed for another user's file.");
                }
            }

            public void SetIgnoredPaths(ICollection<string>? ignoredPaths)
            {
            }

            public Task<BackupFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<Stream> GetFileStreamAsync(
                BackupFileInfo file,
                CancellationToken cancellationToken = default)
            {
                ReadCount++;
                if (content == null)
                {
                    throw new InvalidOperationException("No test content was configured.");
                }

                return Task.FromResult<Stream>(new MemoryStream(content));
            }

            public IEnumerable<string> GetDirectories(
                bool recursive = false,
                CancellationToken cancellationToken = default) => [];

            public IEnumerable<BackupFileInfo> GetFiles(
                bool recursive = false,
                CancellationToken cancellationToken = default) => [];

            public Task<bool?> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
                Task.FromResult<bool?>(content != null);

            public Task<bool?> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task UploadAsync(string path, Stream data, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private class UnexpectedSchedulerFactory : ISchedulerFactory
        {
            public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Scheduler must not be accessed for another user's schedule.");

            public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Scheduler must not be accessed for another user's schedule.");

            public Task<IScheduler?> GetScheduler(
                string schedName,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Scheduler must not be accessed for another user's schedule.");
        }
    }
}
