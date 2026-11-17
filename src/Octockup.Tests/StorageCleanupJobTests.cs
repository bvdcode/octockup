// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Administration;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Enums;
using Quartz;
using Quartz.Impl;
using System.Collections.Specialized;

namespace Octockup.Tests
{
    [Category("Integration")]
    public class StorageCleanupJobTests
    {
        private const string ChunkHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private PostgresTestDatabase _database = null!;

        [OneTimeSetUp]
        public async Task CreateDatabaseAsync()
        {
            _database = await PostgresTestDatabase.CreateAsync();
        }

        [OneTimeTearDown]
        public async Task DropDatabaseAsync()
        {
            await _database.DisposeAsync();
        }

        [Test]
        public async Task ExecuteAsync_DoesNotResumeCleanupFromPreviousProcess()
        {
            await using StorageCleanupTestScenario scenario = await StorageCleanupTestScenario.CreateAsync(
                _database.ConnectionString);
            await scenario.AddQueuedChunkAsync(ChunkHash, [1, 2, 3]);
            await SetRunStateAsync(scenario, StorageCleanupStatus.Running, DateTime.UtcNow.AddDays(-1));
            StorageCleanup previous = await scenario.GetCleanupAsync();
            await scenario.DbContext.StorageCleanups.Where(x => x.Id == previous.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.TotalDeletedChunks, 7)
                    .SetProperty(x => x.TotalReclaimedBytes, 1024));

            await scenario.ExecuteJobAsync();
            await scenario.ExecuteJobAsync();

            StorageCleanup cleanup = await scenario.GetCleanupAsync();
            StorageCleanupRun run = await scenario.GetRunAsync();
            Assert.Multiple(() =>
            {
                Assert.That(cleanup.Status, Is.EqualTo(StorageCleanupStatus.Failed));
                Assert.That(cleanup.ErrorMessage, Does.EndWith("Start it manually to continue."));
                Assert.That(cleanup.TotalDeletedChunks, Is.EqualTo(7));
                Assert.That(cleanup.TotalReclaimedBytes, Is.EqualTo(1024));
                Assert.That(run.Status, Is.EqualTo(StorageCleanupStatus.Failed));
                Assert.That(run.CompletedAt, Is.Not.Null);
                Assert.That(scenario.ChunkExists(ChunkHash), Is.True);
            });
            Assert.That(await scenario.QueuedChunksAsync(), Is.EqualTo([ChunkHash]));
        }

        [TestCase(StorageCleanupStatus.Idle)]
        [TestCase(StorageCleanupStatus.Completed)]
        [TestCase(StorageCleanupStatus.Failed)]
        public async Task ExecuteAsync_DoesNotStartInactiveCleanup(StorageCleanupStatus status)
        {
            await using StorageCleanupTestScenario scenario = await StorageCleanupTestScenario.CreateAsync(
                _database.ConnectionString);
            await scenario.AddQueuedChunkAsync(ChunkHash, [1, 2, 3]);
            await SetRunStateAsync(scenario, status, DateTime.UtcNow.AddDays(-40));

            await scenario.ExecuteJobAsync();

            StorageCleanup cleanup = await scenario.GetCleanupAsync();
            Assert.Multiple(() =>
            {
                Assert.That(cleanup.Status, Is.EqualTo(status));
                Assert.That(cleanup.TotalDeletedChunks, Is.Zero);
                Assert.That(scenario.ChunkExists(ChunkHash), Is.True);
            });
            Assert.That(await scenario.QueuedChunksAsync(), Is.EqualTo([ChunkHash]));
        }

        [Test]
        public async Task ExecuteAsync_QueryFailureStopsRunWithoutAutomaticRetry()
        {
            StorageCleanupFailureInterceptor interceptor = new();
            await using StorageCleanupTestScenario scenario = await StorageCleanupTestScenario.CreateAsync(
                _database.ConnectionString, interceptor);
            await scenario.AddQueuedChunkAsync(ChunkHash, [1, 2, 3]);

            InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
                () => scenario.ExecuteJobAsync());
            Assert.That(exception?.InnerException, Is.TypeOf<TimeoutException>());
            await scenario.ExecuteJobAsync();

            StorageCleanup cleanup = await scenario.GetCleanupAsync();
            StorageCleanupRun run = await scenario.GetRunAsync();
            Assert.Multiple(() =>
            {
                Assert.That(cleanup.Status, Is.EqualTo(StorageCleanupStatus.Failed));
                Assert.That(run.Status, Is.EqualTo(StorageCleanupStatus.Failed));
                Assert.That(run.CompletedAt, Is.Not.Null);
                Assert.That(interceptor.ReferenceQueryCount, Is.EqualTo(1));
                Assert.That(scenario.ChunkExists(ChunkHash), Is.True);
            });
            Assert.That(await scenario.QueuedChunksAsync(), Is.EqualTo([ChunkHash]));
        }

        [Test]
        public async Task ExecuteAsync_ManualStartProcessesBatchesAndDoesNotStartAgainAfterCompletion()
        {
            await using StorageCleanupTestScenario scenario = await StorageCleanupTestScenario.CreateAsync(
                _database.ConnectionString);
            await scenario.AddUploadedChunkAsync(ChunkHash, [1, 2, 3]);
            await SetRunStateAsync(scenario, StorageCleanupStatus.Failed, DateTime.UtcNow.AddDays(-1));
            StorageCleanup cleanup = await scenario.GetCleanupAsync();
            StorageCleanupRun previousRun = await scenario.GetRunAsync();
            ISchedulerFactory schedulerFactory = new StdSchedulerFactory(new NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = $"cleanup-{Guid.NewGuid():N}",
            });
            IScheduler scheduler = await schedulerFactory.GetScheduler();
            try
            {
                IJobDetail job = JobBuilder.Create<StorageCleanupJob>()
                    .WithIdentity(nameof(StorageCleanupJob))
                    .StoreDurably()
                    .Build();
                await scheduler.AddJob(job, replace: false);
                StartStorageCleanupCommandHandler handler = new(scenario.DbContext, schedulerFactory);
                await handler.Handle(new StartStorageCleanupCommand(cleanup.ModuleId), CancellationToken.None);
                scenario.DbContext.ChangeTracker.Clear();

                await scenario.ExecuteJobAsync();
                StorageCleanup firstBatch = await scenario.GetCleanupAsync();
                Assert.That(firstBatch.Status, Is.EqualTo(StorageCleanupStatus.Running));
                await scenario.ExecuteJobAsync();
                await scenario.ExecuteJobAsync();

                StorageCleanup completed = await scenario.GetCleanupAsync();
                StorageCleanupRun currentRun = await scenario.DbContext.StorageCleanupRuns
                    .AsNoTracking().SingleAsync(x => x.Id == completed.LastRunId);
                Assert.Multiple(() =>
                {
                    Assert.That(completed.Status, Is.EqualTo(StorageCleanupStatus.Completed));
                    Assert.That(completed.TotalDeletedChunks, Is.EqualTo(1));
                    Assert.That(completed.TotalReclaimedBytes, Is.EqualTo(3));
                    Assert.That(currentRun.Id, Is.Not.EqualTo(previousRun.Id));
                    Assert.That(currentRun.Status, Is.EqualTo(StorageCleanupStatus.Completed));
                    Assert.That(scenario.ChunkExists(ChunkHash), Is.False);
                });
            }
            finally
            {
                await scheduler.Shutdown();
            }
        }

        private static async Task SetRunStateAsync(
            StorageCleanupTestScenario scenario,
            StorageCleanupStatus status,
            DateTime startedAt)
        {
            StorageCleanup state = await scenario.GetCleanupAsync();
            await scenario.DbContext.StorageCleanups.Where(x => x.Id == state.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.LastStartedAt, startedAt));
            await scenario.DbContext.StorageCleanupRuns.Where(x => x.Id == state.LastRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.StartedAt, startedAt));
        }
    }
}
