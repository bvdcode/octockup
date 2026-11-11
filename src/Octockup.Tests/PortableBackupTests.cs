// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Controllers;
using Octockup.Server.Handlers.Portable;
using Octockup.Server.Models.Enums;
using Octockup.Server.Modules;
using Octockup.Server.Portable;
using Octockup.Server.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;

namespace Octockup.Tests
{
    [Category("Integration")]
    public class PortableBackupTests
    {
        private PostgresTestDatabase _source = null!;
        private PostgresTestDatabase _recipient = null!;
        [OneTimeSetUp]
        public async Task SetupAsync()
        {
            _source = await PostgresTestDatabase.CreateAsync();
            _recipient = await PostgresTestDatabase.CreateAsync();
        }
        [OneTimeTearDown]
        public async Task CleanupAsync()
        {
            await _source.DisposeAsync();
            await _recipient.DisposeAsync();
        }

        [Test]
        public async Task CustomKey_OpensOnFreshInstanceAndRestoresFilesAndFolders()
        {
            const string masterKey = "portable-backup-test-key";
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            await scenario.SetMasterKeyAsync(masterKey);
            byte[] content = new byte[9 * 1024 * 1024 + 13];
            new Random(91).NextBytes(content);
            await scenario.WriteSourceFileAsync("photos/large.dat", content);
            await scenario.WriteSourceFileAsync("photos/empty.txt", []);
            Schedule result = await scenario.RunBackupAsync();
            Assert.That(result.Status, Is.EqualTo(ScheduleStatus.Completed), result.ErrorMessage);
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            SnapshotFile file = await scenario.GetSnapshotFileAsync(snapshot.Id, "photos/large.dat");
            Assert.That(file.ChunkHashes, Has.Count.EqualTo(2));
            Assert.That(file.ChunkHashes.All(x => x.StartsWith("v3-", StringComparison.Ordinal)), Is.True);
            PortablePublicationStore publications = new();
            StorageOperationCoordinator operations = new();
            IBackupProvider provider = new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance);
            PortableStorageFactory publishingFactory = new(scenario.Database, scenario.Cipher, [provider]);
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            SnapshotController controller = new(scenario.Cipher, scenario.Database,
                NullLogger<SnapshotController>.Instance, [provider], operations)
            {
                ControllerContext = new()
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new(new ClaimsIdentity([new Claim("sub", owner.ToString("D"))], "Test")),
                    },
                },
            };
            FileStreamResult normalDownload = (FileStreamResult)await controller.DownloadSnapshotFile(snapshot.Id, file.Id, validate: true);
            await using (Stream normalStream = normalDownload.FileStream)
            {
                using MemoryStream normalBytes = new();
                await normalStream.CopyToAsync(normalBytes);
                Assert.That(normalBytes.ToArray(), Is.EqualTo(content));
            }
            using MemoryStream normalArchive = new();
            controller.Response.Body = normalArchive;
            await controller.DownloadSnapshotArchive(snapshot.Id, default, validate: true);
            normalArchive.Position = 0;
            using (ZipArchive zip = new(normalArchive, ZipArchiveMode.Read, leaveOpen: true))
            {
                Assert.That(zip.Entries.Select(x => x.FullName), Does.Contain("photos/large.dat"));
            }
            PortableCatalogPublisher publisher = new(scenario.Database, scenario.Cipher, publishingFactory, publications, operations);
            await publisher.PublishAsync(owner, scenario.StorageModuleId, default);

            await using PostgresDbContext recipient = new(new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_recipient.ConnectionString).Options);
            IStreamCipher recipientCipher = new AesGcmStreamCipher(RandomNumberGenerator.GetBytes(32));
            User user = new() { Username = "recipient-" + Guid.NewGuid().ToString("N"), PasswordPhc = "not-used" };
            Module target = new() { User = user, Tag = "target-" + Guid.NewGuid().ToString("N"),
                BackupModuleId = provider.Id, Destination = ModuleDestination.Target };
            target.Params(recipientCipher)["path"] = scenario.StorageMount;
            recipient.Add(target);
            await recipient.SaveChangesAsync();
            PortableStorageFactory factory = new(recipient, recipientCipher, [provider]);
            using PortableSessionStore sessions = new();
            UnlockPortableStorageCommandHandler unlock = new(factory, sessions, NullLogger<UnlockPortableStorageCommandHandler>.Instance);
            Assert.ThrowsAsync<AuthApiException>(() => unlock.Handle(new(user.Id, target.Id, "incorrect-key"), default));
            Assert.ThrowsAsync<AuthApiException>(() => unlock.Handle(new(Guid.NewGuid(), target.Id, masterKey), default));
            PortableUnlockResult opened = await unlock.Handle(new(user.Id, target.Id, masterKey), default);
            Assert.That(opened.Snapshots.Select(x => x.Id), Does.Contain(snapshot.Id));
            PortableSessionAccess access = new(sessions, factory);
            PortableAccess storage = await access.OpenAsync(user.Id, opened.Session, default);
            PortableCatalogReader reader = new(NullLogger<PortableCatalogReader>.Instance);
            PortablePage root = await reader.ReadPageAsync(storage, opened.Snapshots.Single(), "", 0, default);
            Assert.That(root.Entries.Single().Path, Is.EqualTo("photos"));
            PortablePage photos = await reader.ReadPageAsync(storage, opened.Snapshots.Single(), "photos", 0, default);
            PortableEntry large = photos.Entries.Single(x => x.Name == "large.dat");
            await using Stream restored = reader.OpenFile(storage, large, default);
            using MemoryStream bytes = new();
            await restored.CopyToAsync(bytes);
            Assert.That(SHA256.HashData(bytes.ToArray()), Is.EqualTo(SHA256.HashData(content)));
            PortableDownload downloads = new(access, reader);
            using MemoryStream archive = new();
            await downloads.WriteFolderAsync(user.Id, new(opened.Session, snapshot.Id, "photos", 0, true), storage, archive, default);
            archive.Position = 0;
            using (ZipArchive zip = new(archive, ZipArchiveMode.Read, leaveOpen: true))
            {
                Assert.That(zip.Entries.Select(x => x.FullName), Is.EquivalentTo(new[] { "large.dat", "empty.txt" }));
                await using Stream zipped = await zip.GetEntry("large.dat")!.OpenAsync();
                using MemoryStream zippedBytes = new();
                await zipped.CopyToAsync(zippedBytes);
                Assert.That(SHA256.HashData(zippedBytes.ToArray()), Is.EqualTo(SHA256.HashData(content)));
            }
            Assert.That(await recipient.Backups.CountAsync(), Is.Zero);
            Assert.That(await recipient.SnapshotFiles.CountAsync(), Is.Zero);
            IBackupStorage publishingStorage = await publishingFactory.OpenAsync(owner, scenario.StorageModuleId, default);
            string catalogPath = PortableFormat.BackupCatalog(snapshot.BackupId);
            await using (Stream catalogStream = await publishingStorage.GetFileStreamAsync(new() { Path = catalogPath }))
            {
                using MemoryStream savedCatalog = new();
                await catalogStream.CopyToAsync(savedCatalog);
                byte[] corrupted = savedCatalog.ToArray();
                corrupted[^1] ^= 1;
                using MemoryStream damaged = new(corrupted);
                await publishingStorage.UploadAsync(catalogPath, damaged);
                Assert.ThrowsAsync<AuthApiException>(() => unlock.Handle(new(user.Id, target.Id, masterKey), default));
                savedCatalog.Position = 0;
                await publishingStorage.UploadAsync(catalogPath, savedCatalog);
            }
            sessions.Remove(user.Id, opened.Session);
            Assert.Throws<AuthApiException>(() => sessions.Get(user.Id, opened.Session));
            await publisher.RemoveAsync(scenario.StorageModuleId, snapshot.Id, default);
            Assert.That(await publishingStorage.ExistsAsync(PortableFormat.CatalogPath), Is.Not.True);
            Assert.That(await publishingStorage.ExistsAsync(PortableFormat.DirectoryPath(snapshot.Id, "photos") + "/directory"), Is.Not.True);
            Assert.ThrowsAsync<AuthApiException>(() => unlock.Handle(new(user.Id, target.Id, masterKey), default));
        }

        [Test]
        public async Task SharedTarget_KeepsDefaultAndCustomKeysIsolated()
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            byte[] content = Encoding.UTF8.GetBytes("identical content must remain recoverable with each backup key");
            await scenario.WriteSourceFileAsync("file.txt", content);
            Assert.That((await scenario.RunBackupAsync()).Status, Is.EqualTo(ScheduleStatus.Completed));
            Snapshot original = await scenario.GetLatestSnapshotAsync();
            SnapshotFile originalFile = await scenario.GetSnapshotFileAsync(original.Id, "file.txt");
            Assert.That(originalFile.ChunkHashes.Single(), Does.Not.StartWith("v3-"));
            foreach (string masterKey in new[] { "first-backup-key", "second-backup-key" })
            {
                await scenario.CreateBackupWithKeyAsync(masterKey);
                Assert.That((await scenario.RunBackupAsync()).Status, Is.EqualTo(ScheduleStatus.Completed));
                Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
                SnapshotFile file = await scenario.GetSnapshotFileAsync(snapshot.Id, "file.txt");
                Assert.That(file.ChunkHashes.Single(), Does.StartWith("v3-" + scenario.BackupId.ToString("N") + "-"));
                Assert.That(await scenario.RestoreFileAsync(snapshot.Id, "file.txt"), Is.EqualTo(content));
                Assert.That(await scenario.RestoreFileAsync(original.Id, "file.txt"), Is.EqualTo(content));
            }
            Assert.That(await scenario.Database.UploadedHashes.CountAsync(x => x.ModuleId == scenario.StorageModuleId), Is.EqualTo(3));
            await scenario.CreateBackupWithKeyAsync("not-started-key");
            IBackupProvider provider = new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance);
            PortableStorageFactory factory = new(scenario.Database, scenario.Cipher, [provider]);
            PortableCatalogPublisher publisher = new(scenario.Database, scenario.Cipher, factory, new(), new());
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            await publisher.PublishAsync(owner, scenario.StorageModuleId, default);
            PortableIndex index = await PortableMetadata.ReadIndexAsync(await factory.OpenAsync(owner, scenario.StorageModuleId, default), default);
            Assert.That(index.Backups, Has.Count.EqualTo(3));
            using PortableSessionStore sessions = new();
            UnlockPortableStorageCommandHandler unlock = new(factory, sessions, NullLogger<UnlockPortableStorageCommandHandler>.Instance);
            PortableUnlockResult first = await unlock.Handle(new(owner, scenario.StorageModuleId, "first-backup-key"), default);
            PortableUnlockResult second = await unlock.Handle(new(owner, scenario.StorageModuleId, "second-backup-key"), default);
            Assert.That(first.Snapshots, Has.Count.EqualTo(1));
            Assert.That(second.Snapshots, Has.Count.EqualTo(1));
            Assert.That(first.Snapshots.Single().BackupId, Is.Not.EqualTo(second.Snapshots.Single().BackupId));
        }

        [Test]
        public async Task DirectoryCatalog_PagesWideFoldersAndKeepsWorkingSetBounded()
        {
            await using PortableTreeSpool spool = new();
            await spool.EnsureDirectoryAsync("", default);
            for (int index = 0; index < 1501; index++)
            {
                await spool.AddAsync(new($"folder/file-{index}", $"file-{index}", false, 0, null, "hash", []), default);
            }
            int files = 0;
            int rootDirectories = 0;
            int pages = 0;
            await foreach ((string Path, List<PortableEntry> Entries, int Page) part in spool.ReadPagesAsync(default))
            {
                Assert.That(part.Entries.Count, Is.LessThanOrEqualTo(PortableFormat.PageSize));
                if (part.Path == "folder") { files += part.Entries.Count; pages++; }
                else { rootDirectories += part.Entries.Count; }
            }
            Assert.That(files, Is.EqualTo(1501));
            Assert.That(pages, Is.EqualTo(4));
            Assert.That(rootDirectories, Is.EqualTo(1));
        }

        [Test]
        public void Session_IsOwnerBoundAndExpires()
        {
            using PortableSessionStore store = new();
            Guid user = Guid.NewGuid();
            string token = store.Add(new(user, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32),
                new(1, []), DateTime.UtcNow.AddMinutes(1)));
            Assert.Throws<AuthApiException>(() => store.Get(Guid.NewGuid(), token));
            Assert.That(store.Get(user, token).UserId, Is.EqualTo(user));
            string expired = store.Add(new(user, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32),
                new(1, []), DateTime.UtcNow.AddSeconds(-1)));
            Assert.Throws<AuthApiException>(() => store.Get(user, expired));
        }
    }
}
