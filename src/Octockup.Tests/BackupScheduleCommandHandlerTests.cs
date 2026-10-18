// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Scheduling;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;

namespace Octockup.Tests
{
    [Category("Integration")]
    public class BackupScheduleCommandHandlerTests
    {
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
        public async Task SetInterval_WhenRepeated_ReusesOneRecurringSchedule()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Backup backup = await SeedBackupAsync(dbContext);
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(dbContext, scheduler, new StorageOperationCoordinator());

            Guid? firstId = await handler.Handle(
                new ManageBackupScheduleCommand(
                    backup.Source.UserId,
                    backup.Id,
                    BackupScheduleAction.SetInterval,
                    60),
                CancellationToken.None);
            Guid? secondId = await handler.Handle(
                new ManageBackupScheduleCommand(
                    backup.Source.UserId,
                    backup.Id,
                    BackupScheduleAction.SetInterval,
                    1_440),
                CancellationToken.None);

            dbContext.ChangeTracker.Clear();
            List<Schedule> schedules = await dbContext.Schedules
                .Where(x => x.BackupId == backup.Id)
                .ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(secondId, Is.EqualTo(firstId));
                Assert.That(schedules, Has.Count.EqualTo(1));
                Assert.That(schedules[0].Interval, Is.EqualTo(TimeSpan.FromDays(1)));
                Assert.That(scheduler.TriggerCount, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task SetInterval_WhenRecurringDuplicatesExist_RemovesDuplicates()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Backup backup = await SeedBackupAsync(dbContext);
            Schedule olderSchedule = new()
            {
                BackupId = backup.Id,
                StartAt = DateTime.UtcNow.AddDays(-4),
                FinishedAt = DateTime.UtcNow.AddDays(-3),
                Status = ScheduleStatus.Completed,
                Interval = TimeSpan.FromDays(1),
            };
            Schedule newerSchedule = new()
            {
                BackupId = backup.Id,
                StartAt = DateTime.UtcNow.AddDays(-2),
                FinishedAt = DateTime.UtcNow.AddDays(-1),
                Status = ScheduleStatus.Completed,
                Interval = TimeSpan.FromHours(1),
            };
            await dbContext.Schedules.AddRangeAsync(olderSchedule, newerSchedule);
            await dbContext.SaveChangesAsync();
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(dbContext, scheduler, new StorageOperationCoordinator());

            Guid? scheduleId = await handler.Handle(
                new ManageBackupScheduleCommand(
                    backup.Source.UserId,
                    backup.Id,
                    BackupScheduleAction.SetInterval,
                    10_080),
                CancellationToken.None);

            dbContext.ChangeTracker.Clear();
            List<Schedule> schedules = await dbContext.Schedules
                .Where(x => x.BackupId == backup.Id && x.Interval != null)
                .ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(schedules, Has.Count.EqualTo(1));
                Assert.That(schedules[0].Id, Is.EqualTo(scheduleId));
                Assert.That(schedules[0].Interval, Is.EqualTo(TimeSpan.FromDays(7)));
                Assert.That(scheduler.TriggerCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task RunNow_WhenRecurringScheduleExists_ReusesItAndPreservesInterval()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Backup backup = await SeedBackupAsync(dbContext);
            Schedule schedule = new()
            {
                BackupId = backup.Id,
                StartAt = DateTime.UtcNow.AddDays(-2),
                FinishedAt = DateTime.UtcNow.AddDays(-1),
                Status = ScheduleStatus.Completed,
                Interval = TimeSpan.FromDays(1),
            };
            await dbContext.Schedules.AddAsync(schedule);
            await dbContext.SaveChangesAsync();
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(dbContext, scheduler, new StorageOperationCoordinator());
            DateTime startedAfter = DateTime.UtcNow.AddSeconds(-1);

            Guid? scheduleId = await handler.Handle(
                new ManageBackupScheduleCommand(
                    backup.Source.UserId,
                    backup.Id,
                    BackupScheduleAction.RunNow),
                CancellationToken.None);

            dbContext.ChangeTracker.Clear();
            Schedule persisted = await dbContext.Schedules.SingleAsync(x => x.BackupId == backup.Id);
            Assert.Multiple(() =>
            {
                Assert.That(scheduleId, Is.EqualTo(schedule.Id));
                Assert.That(persisted.Status, Is.EqualTo(ScheduleStatus.Created));
                Assert.That(persisted.Interval, Is.EqualTo(TimeSpan.FromDays(1)));
                Assert.That(persisted.FinishedAt, Is.Null);
                Assert.That(persisted.StartAt, Is.GreaterThanOrEqualTo(startedAfter));
                Assert.That(scheduler.TriggerCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Disable_WhenRecurringScheduleIsRunning_KeepsCurrentRunAsOneTime()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Backup backup = await SeedBackupAsync(dbContext);
            Schedule schedule = new()
            {
                BackupId = backup.Id,
                StartAt = DateTime.UtcNow,
                Status = ScheduleStatus.Running,
                Interval = TimeSpan.FromHours(1),
            };
            await dbContext.Schedules.AddAsync(schedule);
            await dbContext.SaveChangesAsync();
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(dbContext, scheduler, new StorageOperationCoordinator());

            Guid? scheduleId = await handler.Handle(
                new ManageBackupScheduleCommand(
                    backup.Source.UserId,
                    backup.Id,
                    BackupScheduleAction.Disable),
                CancellationToken.None);

            dbContext.ChangeTracker.Clear();
            Schedule persisted = await dbContext.Schedules.SingleAsync(x => x.Id == schedule.Id);
            Assert.Multiple(() =>
            {
                Assert.That(scheduleId, Is.EqualTo(schedule.Id));
                Assert.That(persisted.Status, Is.EqualTo(ScheduleStatus.Running));
                Assert.That(persisted.Interval, Is.Null);
                Assert.That(scheduler.TriggerCount, Is.Zero);
            });
        }

        [Test]
        public async Task SetInterval_WhenBackupBelongsToAnotherUser_ReturnsNotFound()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Backup backup = await SeedBackupAsync(dbContext);
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(dbContext, scheduler, new StorageOperationCoordinator());

            AuthApiException? exception = Assert.ThrowsAsync<AuthApiException>(() =>
                handler.Handle(
                    new ManageBackupScheduleCommand(
                        Guid.NewGuid(),
                        backup.Id,
                        BackupScheduleAction.SetInterval,
                        60),
                    CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
                Assert.That(scheduler.TriggerCount, Is.Zero);
            });
        }

        private PostgresDbContext CreateDbContext()
        {
            DbContextOptions<PostgresDbContext> options =
                new DbContextOptionsBuilder<PostgresDbContext>()
                    .UseNpgsql(_database.ConnectionString)
                    .Options;
            return new PostgresDbContext(options);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RunNow_WhenDeletionPending_DoesNotCreateSchedule(bool wholeBackup)
        {
            await using PostgresDbContext db = CreateDbContext();
            Backup backup = await SeedBackupAsync(db);
            if (wholeBackup)
            {
                backup.DeletionRequestedAt = DateTime.UtcNow;
            }
            else
            {
                db.Snapshots.Add(new Snapshot { BackupId = backup.Id, DeletionRequestedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
            RecordingBackupJobScheduler scheduler = new();
            ManageBackupScheduleCommandHandler handler = new(db, scheduler, new StorageOperationCoordinator());
            AuthApiException? exception = Assert.ThrowsAsync<AuthApiException>(() => handler.Handle(
                new ManageBackupScheduleCommand(backup.Source.UserId, backup.Id, BackupScheduleAction.RunNow), default));
            Assert.That(exception!.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(await db.Schedules.AnyAsync(x => x.BackupId == backup.Id), Is.False);
            Assert.That(scheduler.TriggerCount, Is.Zero);
        }

        private static async Task<Backup> SeedBackupAsync(AppDbContext dbContext)
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
            await dbContext.Backups.AddAsync(backup);
            await dbContext.SaveChangesAsync();
            return backup;
        }

        private class RecordingBackupJobScheduler : IBackupJobScheduler
        {
            public int TriggerCount { get; private set; }

            public Task TriggerAsync()
            {
                TriggerCount++;
                return Task.CompletedTask;
            }
        }
    }
}
