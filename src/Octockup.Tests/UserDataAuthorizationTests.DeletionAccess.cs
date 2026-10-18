// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Controllers;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Deletion;
using Octockup.Server.Helpers;
using Octockup.Server.Hubs;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public async Task PendingDeletion_BlocksStaleRunnerAndSnapshotReads(bool wholeBackup)
        {
            await using PostgresDbContext db = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(db, includeSchedule: true, includeSnapshot: true);
            if (wholeBackup)
            {
                await new RequestBackupDeletionCommandHandler(db).Handle(new(graph.Source.UserId, graph.Backup.Id), default);
            }
            else
            {
                await new RequestSnapshotDeletionCommandHandler(db).Handle(new(graph.Source.UserId, graph.Snapshot!.Id), default);
            }
            StorageOperationCoordinator operations = new();
            using ServiceProvider services = CreateDeletionServices(db, operations);
            BackupRunner runner = new(CreateCipher(), db, services, NullLogger<BackupRunner>.Instance,
                services.GetRequiredService<IHubContext<EventHub>>(), [], operations);
            await runner.RunAsync(graph.Schedule!, default);
            Assert.That(graph.Schedule!.Status, Is.EqualTo(ScheduleStatus.Running));
            Assert.That(await ScheduleHelpers.GetNextScheduleAsync(db.Schedules.Where(x => x.Id == graph.Schedule.Id), default), Is.Null);
            SnapshotController controller = AsUser(new SnapshotController(CreateCipher(), db,
                NullLogger<SnapshotController>.Instance, [], operations), graph.Source.UserId);
            Assert.That(await controller.DownloadSnapshotArchive(graph.Snapshot!.Id, default), Is.InstanceOf<NotFoundResult>());
            Assert.That(await controller.DownloadSnapshotFile(graph.Snapshot.Id, graph.SnapshotFile!.Id), Is.InstanceOf<NotFoundResult>());
            Assert.That(await controller.GetSnapshot(graph.Snapshot.Id), Is.InstanceOf<NotFoundResult>());
            OkObjectResult listing = (OkObjectResult)await controller.GetSnapshots(graph.Backup.Id);
            List<SnapshotDto> snapshots = (List<SnapshotDto>)listing.Value!;
            Assert.That(snapshots.Single().DeletionRequestedAt, Is.Not.Null);
            await ProcessDeletionsAsync(db, services);
        }
    }
}
