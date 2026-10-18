// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Administration;
using Octockup.Server.Models.Enums;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        [TestCase(31, null, null, true)]
        [TestCase(29, null, null, false)]
        [TestCase(60, StorageCleanupStatus.Completed, 31, true)]
        [TestCase(60, StorageCleanupStatus.Completed, 29, false)]
        [TestCase(60, StorageCleanupStatus.Failed, 31, true)]
        [TestCase(60, StorageCleanupStatus.Running, 31, false)]
        public async Task CleanupSchedule_UsesPersistedAgeAndDoesNotRestartActiveRuns(
            int storageAgeDays,
            StorageCleanupStatus? status,
            int? lastRunAgeDays,
            bool expectedDue)
        {
            await using PostgresDbContext seedContext = CreateDbContext();
            Module storage = await SeedStandaloneModuleAsync(seedContext);
            DateTime createdAt = DateTime.UtcNow.AddDays(-storageAgeDays);
            await seedContext.Modules.Where(x => x.Id == storage.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CreatedAt, createdAt));
            if (status.HasValue)
            {
                seedContext.StorageCleanups.Add(new StorageCleanup
                {
                    ModuleId = storage.Id,
                    Status = status.Value,
                    LastStartedAt = DateTime.UtcNow.AddDays(-lastRunAgeDays!.Value),
                });
                await seedContext.SaveChangesAsync();
            }

            await using PostgresDbContext restartedContext = CreateDbContext();
            GetDueStorageCleanupsQueryHandler handler = new(restartedContext);
            Guid[] due = await handler.Handle(new GetDueStorageCleanupsQuery(), CancellationToken.None);
            Assert.That(due.Contains(storage.Id), Is.EqualTo(expectedDue));

            await restartedContext.Modules.Where(x => x.Id == storage.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Destination, ModuleDestination.Source));
            Guid[] afterChangingToSource = await handler.Handle(
                new GetDueStorageCleanupsQuery(), CancellationToken.None);
            Assert.That(afterChangingToSource, Does.Not.Contain(storage.Id));
        }
    }
}
