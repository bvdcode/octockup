// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.Mvc;
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
        public async Task GetSnapshots_ReturnsPersistedSummariesWithoutReadingFileRows()
        {
            await using PostgresDbContext seedContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(seedContext, includeSnapshot: true);
            Snapshot emptySnapshot = new() { BackupId = graph.Backup.Id };
            seedContext.Snapshots.Add(emptySnapshot);
            await seedContext.SaveChangesAsync();

            List<string> queries = [];
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .LogTo(queries.Add, [RelationalEventId.CommandExecuted])
                .Options;
            await using PostgresDbContext dbContext = new(options);
            SnapshotController controller = AsUser(
                new SnapshotController(CreateCipher(), dbContext, NullLogger<SnapshotController>.Instance, []),
                graph.Source.UserId);

            IActionResult result = await controller.GetSnapshots(graph.Backup.Id);
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            List<SnapshotDto> snapshots = (List<SnapshotDto>)((OkObjectResult)result).Value!;
            SnapshotDto completed = snapshots.Single(x => x.Id == graph.Snapshot!.Id);
            SnapshotDto empty = snapshots.Single(x => x.Id == emptySnapshot.Id);
            Assert.Multiple(() =>
            {
                Assert.That(snapshots, Has.Count.EqualTo(2));
                Assert.That(completed.FilesCount, Is.EqualTo(1));
                Assert.That(completed.TotalSize, Is.EqualTo(1));
                Assert.That(empty.FilesCount, Is.Zero);
                Assert.That(empty.TotalSize, Is.Zero);
                Assert.That(queries, Has.Count.EqualTo(2));
                Assert.That(queries.Any(x => x.Contains("snapshot_files", StringComparison.Ordinal)), Is.False);
                Assert.That(dbContext.ChangeTracker.Entries(), Is.Empty);
            });
        }
    }
}
