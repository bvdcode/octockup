// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Deletion;
using Octockup.Server.Hubs;
using Octockup.Server.Modules;
using Octockup.Server.Portable;

namespace Octockup.Tests
{
    public partial class PortableBackupTests
    {
        [Test]
        public async Task UnpublishedSnapshot_DeletesWithoutWritingAnotherBackupsCatalog()
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            await scenario.WriteSourceFileAsync("file.txt", [1, 2, 3]);
            await scenario.RunBackupAsync();
            Snapshot legacy = await scenario.GetLatestSnapshotAsync();
            await scenario.CreateBackupWithKeyAsync("published-backup-key");
            await scenario.RunBackupAsync();
            Snapshot published = await scenario.GetLatestSnapshotAsync();
            FaultInjectingStorage storage = new(new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance));
            PortableStorageFactory factory = new(scenario.Database, scenario.Cipher, [storage]);
            PortableCatalogPublisher publisher = new(scenario.Database, scenario.Cipher, factory, new(), new());
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            await publisher.PublishAsync(owner, scenario.StorageModuleId, default, customKeysOnly: true);
            PortableIndex before = await PortableMetadata.ReadIndexAsync(storage, default);
            Assert.That(before.Backups, Is.EqualTo(new[] { published.BackupId }));
            int uploadCount = storage.Uploads.Count;
            storage.FailUpload = _ => true;

            Assert.That(await DeleteSnapshotAsync(scenario, publisher, legacy.Id), Is.EqualTo(1));

            bool legacyRemains = await scenario.Database.Snapshots.AnyAsync(x => x.Id == legacy.Id);
            bool publishedRemains = await scenario.Database.Snapshots.AnyAsync(x => x.Id == published.Id);
            Assert.Multiple(() =>
            {
                Assert.That(storage.Uploads, Has.Count.EqualTo(uploadCount));
                Assert.That(legacyRemains, Is.False);
                Assert.That(publishedRemains, Is.True);
            });
            PortableIndex after = await PortableMetadata.ReadIndexAsync(storage, default);
            Assert.That(after.Backups, Is.EqualTo(before.Backups));
        }

        [TestCase("page", false)]
        [TestCase("second-page", false)]
        [TestCase("catalog", false)]
        [TestCase("index", false)]
        [TestCase("catalog", true)]
        public async Task InterruptedPublication_DeletionRemovesAllGenerationObjects(string failure, bool retryDeletion)
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            await scenario.SetMasterKeyAsync("interrupted-backup-key");
            await scenario.WriteSourceFileAsync("folder/one.txt", [1]);
            await scenario.WriteSourceFileAsync("folder/nested/two.txt", [2]);
            if (failure == "second-page")
            {
                for (int index = 0; index <= PortableFormat.PageSize; index++)
                {
                    await scenario.WriteSourceFileAsync($"wide/file-{index}.txt", []);
                }
            }
            await scenario.RunBackupAsync();
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            string generation = $"{PortableFormat.Root}/{snapshot.Id:N}/";
            string failedObject = failure switch
            {
                "page" => PortableFormat.DirectoryPath(snapshot.Id, "folder") + "/0",
                "second-page" => PortableFormat.DirectoryPath(snapshot.Id, "wide") + "/1",
                "catalog" => PortableFormat.BackupCatalog(snapshot.BackupId),
                "index" => PortableFormat.CatalogPath,
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            };
            FaultInjectingStorage storage = new(new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance))
            {
                FailUpload = path => path == failedObject,
            };
            PortableStorageFactory factory = new(scenario.Database, scenario.Cipher, [storage]);
            PortableCatalogPublisher publisher = new(scenario.Database, scenario.Cipher, factory, new(), new());
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(owner, scenario.StorageModuleId, default));
            Assert.That(storage.Objects.Any(x => x.StartsWith(generation, StringComparison.Ordinal)), Is.True);
            storage.FailUpload = null;
            publisher = new(scenario.Database, scenario.Cipher, factory, new(), new());
            if (retryDeletion)
            {
                storage.FailDelete = path => path == PortableFormat.DirectoryPath(snapshot.Id, "folder/nested") + "/0";
                Assert.That(await DeleteSnapshotAsync(scenario, publisher, snapshot.Id), Is.Zero);
                Assert.That(await scenario.Database.SnapshotFiles.AnyAsync(x => x.SnapshotId == snapshot.Id), Is.True);
                storage.FailDelete = null;
                await scenario.Database.Snapshots.Where(x => x.Id == snapshot.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeletionError, (string?)null));
            }

            Assert.That(await DeleteSnapshotAsync(scenario, publisher, snapshot.Id), Is.EqualTo(1));

            bool snapshotRemains = await scenario.Database.Snapshots.AnyAsync(x => x.Id == snapshot.Id);
            Assert.Multiple(() =>
            {
                Assert.That(storage.Objects.Any(x => x.StartsWith(generation, StringComparison.Ordinal)), Is.False);
                Assert.That(snapshotRemains, Is.False);
            });
        }

        private static async Task<int> DeleteSnapshotAsync(BackupIntegrationScenario scenario,
            PortableCatalogPublisher publisher, Guid snapshotId)
        {
            await scenario.Database.Snapshots.Where(x => x.Id == snapshotId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeletionRequestedAt, DateTime.UtcNow));
            ProcessPendingDeletionsCommandHandler handler = new(scenario.Database, new(),
                scenario.Services.GetRequiredService<IHubContext<EventHub>>(),
                NullLogger<ProcessPendingDeletionsCommandHandler>.Instance, publisher);
            return await handler.Handle(new(), default);
        }
    }
}
