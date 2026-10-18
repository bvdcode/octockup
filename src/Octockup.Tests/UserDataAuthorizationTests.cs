// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using EasyExtensions.Models.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Controllers;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Enums;
using Octockup.Server.Models.Requests;
using Quartz;
using EasyExtensions.Mediator;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Octockup.Tests
{
    [Category("Integration")]
    public partial class UserDataAuthorizationTests
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
        public async Task RenameModule_WhenOwnedByAnotherUser_ReturnsNotFoundAndDoesNotRename()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Module module = await SeedStandaloneModuleAsync(dbContext);
            ModuleController controller = AsUser(
                new ModuleController(CreateCipher(), dbContext, NullLogger<ModuleController>.Instance, []),
                Guid.NewGuid());
            string originalTag = module.Tag;

            IActionResult result = await controller.RenameModule(
                module.Id,
                new RenameModuleRequest { NewTag = $"renamed-{Guid.NewGuid():N}" });

            AssertNotFound(result);
            dbContext.ChangeTracker.Clear();
            Module persisted = await dbContext.Modules.SingleAsync(x => x.Id == module.Id);
            Assert.That(persisted.Tag, Is.EqualTo(originalTag));
        }

        [Test]
        public async Task DeleteModule_WhenOwnedByAnotherUser_ReturnsNotFoundAndDoesNotDelete()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Module module = await SeedStandaloneModuleAsync(dbContext);
            ModuleController controller = AsUser(
                new ModuleController(CreateCipher(), dbContext, NullLogger<ModuleController>.Instance, []),
                Guid.NewGuid());

            IActionResult result = await controller.DeleteUserBackupStorage(module.Id);

            AssertNotFound(result);
            dbContext.ChangeTracker.Clear();
            Assert.That(await dbContext.Modules.AnyAsync(x => x.Id == module.Id), Is.True);
        }

        [Test]
        public async Task RenameModule_WhenOwnedByCurrentUser_RenamesModule()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Module module = await SeedStandaloneModuleAsync(dbContext);
            ModuleController controller = AsUser(
                new ModuleController(CreateCipher(), dbContext, NullLogger<ModuleController>.Instance, []),
                module.UserId);
            string newTag = $"renamed-{Guid.NewGuid():N}";

            IActionResult result = await controller.RenameModule(
                module.Id,
                new RenameModuleRequest { NewTag = newTag });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            dbContext.ChangeTracker.Clear();
            Module persisted = await dbContext.Modules.SingleAsync(x => x.Id == module.Id);
            Assert.That(persisted.Tag, Is.EqualTo(newTag));
        }

        [Test]
        public async Task DeleteModule_WhenCleanupCompleted_RemovesCleanupState()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Module module = await SeedStandaloneModuleAsync(dbContext);
            StorageCleanup cleanup = new()
            {
                ModuleId = module.Id,
                Status = StorageCleanupStatus.Completed,
            };
            StorageCleanupRun run = new()
            {
                ModuleId = module.Id,
                Status = StorageCleanupStatus.Completed,
                StartedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = DateTime.UtcNow,
            };
            await dbContext.AddRangeAsync(cleanup, run);
            await dbContext.SaveChangesAsync();
            ModuleController controller = AsUser(
                new ModuleController(CreateCipher(), dbContext, NullLogger<ModuleController>.Instance, []),
                module.UserId);

            IActionResult result = await controller.DeleteUserBackupStorage(module.Id);

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            dbContext.ChangeTracker.Clear();
            bool moduleExists = await dbContext.Modules.AnyAsync(x => x.Id == module.Id);
            bool cleanupExists = await dbContext.StorageCleanups.AnyAsync(x => x.ModuleId == module.Id);
            bool cleanupRunExists = await dbContext.StorageCleanupRuns.AnyAsync(x => x.ModuleId == module.Id);
            Assert.Multiple(() =>
            {
                Assert.That(moduleExists, Is.False);
                Assert.That(cleanupExists, Is.False);
                Assert.That(cleanupRunExists, Is.False);
            });
        }

        [Test]
        public async Task DeleteModule_WhenCleanupRunning_ReturnsConflict()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            Module module = await SeedStandaloneModuleAsync(dbContext);
            StorageCleanup cleanup = new()
            {
                ModuleId = module.Id,
                Status = StorageCleanupStatus.Running,
            };
            await dbContext.StorageCleanups.AddAsync(cleanup);
            await dbContext.SaveChangesAsync();
            ModuleController controller = AsUser(
                new ModuleController(CreateCipher(), dbContext, NullLogger<ModuleController>.Instance, []),
                module.UserId);

            IActionResult result = await controller.DeleteUserBackupStorage(module.Id);

            ObjectResult conflict = result as ObjectResult
                ?? throw new AssertionException("Expected an object result.");
            Assert.That(conflict.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            dbContext.ChangeTracker.Clear();
            Assert.That(await dbContext.Modules.AnyAsync(x => x.Id == module.Id), Is.True);
        }

        [Test]
        public async Task DeleteBackup_WhenOwnedByAnotherUser_ReturnsNotFoundAndDoesNotDelete()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(dbContext);
            BackupController controller = AsUser(
                new BackupController(
                    dbContext,
                    CreateCipher(),
                    new UnexpectedSchedulerFactory(),
                    NullLogger<BackupController>.Instance),
                Guid.NewGuid());

            using ServiceProvider services = CreateDeletionServices(dbContext);
            IActionResult result = await controller.DeleteBackup(graph.Backup.Id, services.GetRequiredService<IMediator>());

            AssertNotFound(result);
            dbContext.ChangeTracker.Clear();
            Assert.That(await dbContext.Backups.AnyAsync(x => x.Id == graph.Backup.Id), Is.True);
        }

        [Test]
        public async Task UpdateBackup_WhenOwnedByAnotherUser_ReturnsNotFoundAndDoesNotMutate()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(dbContext);
            BackupController controller = AsUser(
                new BackupController(
                    dbContext,
                    CreateCipher(),
                    new UnexpectedSchedulerFactory(),
                    NullLogger<BackupController>.Instance),
                Guid.NewGuid());
            string originalTag = graph.Backup.Tag;

            IActionResult renameResult = await controller.RenameBackup(
                graph.Backup.Id,
                new RenameModuleRequest { NewTag = $"renamed-{Guid.NewGuid():N}" });
            IActionResult ignoredPathsResult = await controller.UpdateIgnoredPaths(
                graph.Backup.Id,
                ["private/path"]);

            AssertNotFound(renameResult);
            AssertNotFound(ignoredPathsResult);
            dbContext.ChangeTracker.Clear();
            Backup persisted = await dbContext.Backups.SingleAsync(x => x.Id == graph.Backup.Id);
            Assert.Multiple(() =>
            {
                Assert.That(persisted.Tag, Is.EqualTo(originalTag));
                Assert.That(persisted.IgnoredPaths, Is.Empty);
            });
        }

        [Test]
        public async Task CancelSchedule_WhenOwnedByAnotherUser_ReturnsNotFoundBeforeUsingScheduler()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(dbContext, includeSchedule: true);
            ScheduleController controller = AsUser(
                new ScheduleController(dbContext, new UnexpectedSchedulerFactory()),
                Guid.NewGuid());

            IActionResult result = await controller.CancelSchedule(graph.Schedule!.Id);

            AssertNotFound(result);
        }

        [Test]
        public async Task DeleteBackup_WhenOwnedByCurrentUser_DeletesDependentMetadata()
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(
                dbContext,
                includeSchedule: true,
                includeSnapshot: true);
            BackupController controller = AsUser(
                new BackupController(
                    dbContext,
                    CreateCipher(),
                    new UnexpectedSchedulerFactory(),
                    NullLogger<BackupController>.Instance),
                graph.Source.UserId);

            using ServiceProvider services = CreateDeletionServices(dbContext);
            IActionResult result = await controller.DeleteBackup(graph.Backup.Id, services.GetRequiredService<IMediator>());

            Assert.That(result, Is.InstanceOf<AcceptedResult>());
            Assert.That(await dbContext.SnapshotFiles.AnyAsync(x => x.SnapshotId == graph.Snapshot!.Id), Is.True);
            await ProcessDeletionsAsync(dbContext, services);
            dbContext.ChangeTracker.Clear();
            bool backupExists = await dbContext.Backups.AnyAsync(x => x.Id == graph.Backup.Id);
            bool scheduleExists = await dbContext.Schedules.AnyAsync(x => x.Id == graph.Schedule!.Id);
            bool snapshotExists = await dbContext.Snapshots.AnyAsync(x => x.Id == graph.Snapshot!.Id);
            bool snapshotFileExists = await dbContext.SnapshotFiles.AnyAsync(x => x.Id == graph.SnapshotFile!.Id);
            bool sourceExists = await dbContext.Modules.AnyAsync(x => x.Id == graph.Source.Id);
            bool storageExists = await dbContext.Modules.AnyAsync(x => x.Id == graph.Storage.Id);

            Assert.Multiple(() =>
            {
                Assert.That(backupExists, Is.False);
                Assert.That(scheduleExists, Is.False);
                Assert.That(snapshotExists, Is.False);
                Assert.That(snapshotFileExists, Is.False);
                Assert.That(sourceExists, Is.True);
                Assert.That(storageExists, Is.True);
            });
        }

    }
}
