// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Deletion;
using Octockup.Server.Hubs;
using Octockup.Server.Services;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        private static ServiceProvider CreateDeletionServices(AppDbContext dbContext, StorageOperationCoordinator? operations = null)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddSignalR();
            services.AddMediator(config => config.RegisterServicesFromAssemblyContaining<RequestBackupDeletionCommand>());
            services.AddSingleton(dbContext);
            services.AddSingleton(operations ?? new StorageOperationCoordinator());
            return services.BuildServiceProvider();
        }

        private static Task<int> ProcessDeletionsAsync(AppDbContext dbContext, ServiceProvider services, CancellationToken token = default)
        {
            ProcessPendingDeletionsCommandHandler handler = new(dbContext,
                services.GetRequiredService<StorageOperationCoordinator>(),
                services.GetRequiredService<IHubContext<EventHub>>(),
                NullLogger<ProcessPendingDeletionsCommandHandler>.Instance);
            return handler.Handle(new ProcessPendingDeletionsCommand(), token);
        }

        [Test]
        public async Task SnapshotDeletion_WaitsForReadersAndPreservesOtherSnapshotsAndChunks()
        {
            await using PostgresDbContext db = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(db, includeSnapshot: true);
            Snapshot other = new() { BackupId = graph.Backup.Id };
            SnapshotFile shared = new()
            {
                Snapshot = other, Path = "shared", Name = "shared", Hashsum = "shared-hash", ChunkHashes = ["shared-hash"],
            };
            graph.SnapshotFile!.ChunkHashes = ["shared-hash"];
            UploadedHash hash = new() { ModuleId = graph.Storage.Id, Hash = "shared-hash" };
            db.AddRange(other, shared, hash);
            await db.SaveChangesAsync();
            StorageOperationCoordinator operations = new();
            using ServiceProvider services = CreateDeletionServices(db, operations);
            await using (StorageOperationLease lease = await operations.AcquireSnapshotUseAsync(graph.Backup.Id, default))
            {
                RequestSnapshotDeletionCommandHandler request = new(db);
                Assert.That(await request.Handle(new(graph.Source.UserId, graph.Snapshot!.Id), default), Is.True);
                Assert.That(await ProcessDeletionsAsync(db, services), Is.Zero);
                Assert.That(await db.SnapshotFiles.AnyAsync(x => x.SnapshotId == graph.Snapshot.Id), Is.True);
            }
            Assert.That(await ProcessDeletionsAsync(db, services), Is.EqualTo(1));
            Assert.That(await db.Snapshots.AnyAsync(x => x.Id == graph.Snapshot!.Id), Is.False);
            Assert.That(await db.SnapshotFiles.AnyAsync(x => x.Id == shared.Id), Is.True);
            Assert.That(await db.UploadedHashes.AnyAsync(x => x.Id == hash.Id), Is.True);
            Assert.That(await db.Backups.AnyAsync(x => x.Id == graph.Backup.Id), Is.True);
        }

        [Test]
        public async Task DeletionRequest_IsIdempotentAndSurvivesNewContext()
        {
            Guid backupId;
            Guid userId;
            DateTime? requestedAt;
            await using (PostgresDbContext db = CreateDbContext())
            {
                OwnedGraph graph = await SeedOwnedGraphAsync(db, includeSchedule: true, includeSnapshot: true);
                backupId = graph.Backup.Id;
                userId = graph.Source.UserId;
                RequestBackupDeletionCommandHandler request = new(db);
                await request.Handle(new(userId, backupId), default);
                requestedAt = await db.Backups.Where(x => x.Id == backupId).Select(x => x.DeletionRequestedAt).SingleAsync();
                await request.Handle(new(userId, backupId), default);
                Assert.That(await db.Backups.Where(x => x.Id == backupId).Select(x => x.DeletionRequestedAt).SingleAsync(), Is.EqualTo(requestedAt));
                Assert.That(await db.SnapshotFiles.AnyAsync(x => x.SnapshotId == graph.Snapshot!.Id), Is.True);
            }
            await using PostgresDbContext resumed = CreateDbContext();
            using ServiceProvider services = CreateDeletionServices(resumed);
            await ProcessDeletionsAsync(resumed, services);
            Assert.That(await resumed.Backups.AnyAsync(x => x.Id == backupId), Is.False);
        }
    }
}
