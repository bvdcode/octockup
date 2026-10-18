// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Controllers;
using Octockup.Server.Database;
using Octockup.Server.Models.Dto;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        [Test]
        public async Task GetUserBackups_LoadsCollectionsSeparatelyWithoutChangingTheirContents()
        {
            await using PostgresDbContext seedContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(seedContext, includeSchedule: true, includeSnapshot: true);
            seedContext.Snapshots.Add(new Snapshot { BackupId = graph.Backup.Id });
            seedContext.Schedules.Add(new Schedule { BackupId = graph.Backup.Id });
            await seedContext.SaveChangesAsync();
            await SeedOwnedGraphAsync(seedContext, includeSchedule: true, includeSnapshot: true);

            List<string> queries = [];
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .LogTo(queries.Add, [RelationalEventId.CommandExecuted])
                .Options;
            await using PostgresDbContext dbContext = new(options);
            BackupController controller = AsUser(
                new BackupController(dbContext, CreateCipher(), new UnexpectedSchedulerFactory(),
                    NullLogger<BackupController>.Instance),
                graph.Source.UserId);

            List<BackupDto> backups = (await controller.GetUserBackups()).ToList();

            Assert.That(backups, Has.Count.EqualTo(1));
            BackupDto backup = backups.Single();
            Assert.Multiple(() =>
            {
                Assert.That(backup.Id, Is.EqualTo(graph.Backup.Id));
                Assert.That(backup.Source.Id, Is.EqualTo(graph.Source.Id));
                Assert.That(backup.Storage.Id, Is.EqualTo(graph.Storage.Id));
                Assert.That(backup.Snapshots.Select(x => x.Id).Distinct().Count(), Is.EqualTo(2));
                Assert.That(backup.Schedules.Select(x => x.Id).Distinct().Count(), Is.EqualTo(2));
                Assert.That(backup.Schedules.All(x => x.Backup.Snapshots.Count == 2), Is.True);
                Assert.That(queries, Has.Count.EqualTo(4));
                Assert.That(queries.Any(x => x.Contains("snapshot_files", StringComparison.Ordinal)), Is.False);
                Assert.That(dbContext.ChangeTracker.Entries(), Is.Empty);
            });
        }
    }
}
