// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Deletion;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        [Test]
        public async Task Deletion_CommitsBoundedBatchesAndResumesAfterCancellation()
        {
            Guid snapshotId;
            await using (PostgresDbContext seed = CreateDbContext())
            {
                OwnedGraph graph = await SeedOwnedGraphAsync(seed, includeSnapshot: true);
                snapshotId = graph.Snapshot!.Id;
                seed.SnapshotFiles.AddRange(Enumerable.Range(0, 20_000).Select(index => new SnapshotFile
                {
                    SnapshotId = snapshotId, Path = $"file-{index:D8}", Name = "file", Size = 1, Hashsum = "hash",
                }));
                graph.Snapshot.DeletionRequestedAt = DateTime.UtcNow;
                await seed.SaveChangesAsync();
            }
            using CancellationTokenSource cancellation = new();
            DeletionBatchInterceptor interceptor = new() { CancelAfterBatch = cancellation };
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString).AddInterceptors(interceptor).Options;
            await using (PostgresDbContext interrupted = new(options))
            {
                using ServiceProvider services = CreateDeletionServices(interrupted);
                Assert.ThrowsAsync<OperationCanceledException>(() => ProcessDeletionsAsync(interrupted, services, cancellation.Token));
            }
            await using PostgresDbContext resumed = CreateDbContext();
            Assert.That(interceptor.DeletedBatchSizes, Is.EqualTo(new[] { 10_000 }));
            Assert.That(interceptor.Commands, Does.Not.Contain("ANALYZE snapshot_files"));
            Assert.That(await resumed.SnapshotFiles.CountAsync(x => x.SnapshotId == snapshotId), Is.EqualTo(10_001));
            using ServiceProvider resumedServices = CreateDeletionServices(resumed);
            await ProcessDeletionsAsync(resumed, resumedServices);
            Assert.That(await resumed.Snapshots.AnyAsync(x => x.Id == snapshotId), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Deletion_RefreshesStatisticsBeforeRemovingSnapshot(bool filesAlreadyDeleted)
        {
            await using PostgresDbContext seed = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(seed, includeSnapshot: true);
            Guid snapshotId = graph.Snapshot!.Id;
            if (filesAlreadyDeleted)
            {
                await seed.SnapshotFiles.Where(x => x.SnapshotId == snapshotId).ExecuteDeleteAsync();
            }
            await new RequestSnapshotDeletionCommandHandler(seed).Handle(new(graph.Source.UserId, snapshotId), default);
            DeletionBatchInterceptor interceptor = new();
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString).AddInterceptors(interceptor).Options;
            await using PostgresDbContext processing = new(options);
            using ServiceProvider services = CreateDeletionServices(processing);
            Assert.That(await ProcessDeletionsAsync(processing, services), Is.EqualTo(1));
            int analyzeIndex = interceptor.Commands.IndexOf("ANALYZE snapshot_files");
            int lastFileDeleteIndex = interceptor.Commands.FindLastIndex(x => x.StartsWith("DELETE FROM snapshot_files ", StringComparison.Ordinal));
            int snapshotDeleteIndex = interceptor.Commands.FindIndex(x => x.StartsWith("DELETE FROM snapshots ", StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(interceptor.Commands.Count(x => x == "ANALYZE snapshot_files"), Is.EqualTo(1));
                Assert.That(analyzeIndex, Is.GreaterThan(lastFileDeleteIndex));
                Assert.That(snapshotDeleteIndex, Is.GreaterThan(analyzeIndex));
            });
            Assert.That(await seed.Snapshots.AnyAsync(x => x.Id == snapshotId), Is.False);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public async Task DeletionFailure_PersistsErrorAndCanBeRetried(bool wholeBackup, bool automaticRetry)
        {
            await using PostgresDbContext seed = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(seed, includeSnapshot: true);
            RequestSnapshotDeletionCommandHandler request = new(seed);
            RequestBackupDeletionCommandHandler backupRequest = new(seed);
            if (wholeBackup)
            {
                await backupRequest.Handle(new(graph.Source.UserId, graph.Backup.Id), default);
            }
            else
            {
                await request.Handle(new(graph.Source.UserId, graph.Snapshot!.Id), default);
            }
            DeletionBatchInterceptor interceptor = new() { FailSnapshotDeletion = true };
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString).AddInterceptors(interceptor).Options;
            await using (PostgresDbContext failing = new(options))
            {
                using ServiceProvider services = CreateDeletionServices(failing);
                await ProcessDeletionsAsync(failing, services);
            }
            string? error = wholeBackup
                ? await seed.Backups.Where(x => x.Id == graph.Backup.Id).Select(x => x.DeletionError).SingleAsync()
                : await seed.Snapshots.Where(x => x.Id == graph.Snapshot!.Id).Select(x => x.DeletionError).SingleAsync();
            Assert.That(error, Is.EqualTo(nameof(InvalidOperationException)));
            using ServiceProvider retryServices = CreateDeletionServices(seed);
            Assert.That(await ProcessDeletionsAsync(seed, retryServices), Is.Zero);
            if (automaticRetry)
            {
                DateTime retryDue = DateTime.UtcNow.AddMinutes(-6);
                await seed.Backups.Where(x => x.Id == graph.Backup.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, retryDue));
                await seed.Snapshots.Where(x => x.Id == graph.Snapshot!.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, retryDue));
            }
            else if (wholeBackup)
            {
                await backupRequest.Handle(new(graph.Source.UserId, graph.Backup.Id), default);
            }
            else
            {
                await request.Handle(new(graph.Source.UserId, graph.Snapshot!.Id), default);
            }
            Assert.That(await ProcessDeletionsAsync(seed, retryServices), Is.EqualTo(wholeBackup ? 2 : 1));
        }
    }
}
