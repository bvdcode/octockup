// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Controllers;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Portable;
using Octockup.Server.Modules;
using Octockup.Server.Portable;
using Octockup.Server.Services;
using System.IO.Compression;
using System.Security.Claims;

namespace Octockup.Tests
{
    public partial class PortableBackupTests
    {
        [TestCase("", true)]
        [TestCase("folder", true)]
        [TestCase("folder/file.txt", false)]
        public async Task PortableDownload_HttpFormRestoresRootFolderAndFile(string path, bool folder)
        {
            const string masterKey = "http-download-backup-key";
            byte[] content = [1, 2, 3];
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            await scenario.SetMasterKeyAsync(masterKey);
            await scenario.WriteSourceFileAsync("folder/file.txt", content);
            await scenario.RunBackupAsync();
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            IBackupProvider provider = new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance);
            PortableStorageFactory factory = new(scenario.Database, scenario.Cipher, [provider]);
            PortableCatalogPublisher publisher = new(scenario.Database, scenario.Cipher, factory, new(), new());
            await publisher.PublishAsync(owner, scenario.StorageModuleId, default);
            using PortableSessionStore sessions = new();
            UnlockPortableStorageCommandHandler unlock = new(factory, sessions, NullLogger<UnlockPortableStorageCommandHandler>.Instance);
            PortableUnlockResult opened = await unlock.Handle(new(owner, scenario.StorageModuleId, masterKey), default);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Octockup.Server" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers();
            builder.Services.AddAuthorization();
            builder.Services.AddMediator(config => config.RegisterServicesFromAssemblyContaining<ReadPortableDirectoryQuery>());
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton<PortableSessionAccess>();
            builder.Services.AddSingleton<PortableCatalogReader>();
            builder.Services.AddSingleton<PortableDownload>();
            builder.Services.AddSingleton<StorageOperationCoordinator>();
            await using WebApplication app = builder.Build();
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                context.User = new(new ClaimsIdentity([new Claim("sub", owner.ToString())], "Test"));
                await next(context);
            });
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            using HttpClient client = app.GetTestClient();
            using FormUrlEncodedContent form = new(new Dictionary<string, string>
            {
                ["Session"] = opened.Session,
                ["SnapshotId"] = snapshot.Id.ToString(),
                ["Path"] = path,
                ["Page"] = "0",
                ["Folder"] = folder.ToString(),
            });

            using HttpResponseMessage response = await client.PostAsync("/api/v1/storage-browser/download", form);
            byte[] downloaded = await response.Content.ReadAsByteArrayAsync();
            Assert.That(response.IsSuccessStatusCode, Is.True, System.Text.Encoding.UTF8.GetString(downloaded));
            if (folder)
            {
                using MemoryStream bytes = new(downloaded);
                using ZipArchive zip = new(bytes, ZipArchiveMode.Read);
                string name = path.Length == 0 ? "folder/file.txt" : "file.txt";
                Assert.That(zip.Entries.Select(x => x.FullName), Is.EqualTo(new[] { name }));
                await using Stream file = await zip.Entries.Single().OpenAsync();
                using MemoryStream restored = new();
                await file.CopyToAsync(restored);
                Assert.That(restored.ToArray(), Is.EqualTo(content));
            }
            else
            {
                Assert.That(downloaded, Is.EqualTo(content));
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(content.Length));
            }
        }

        [Test]
        public async Task CustomKeyArchive_DecryptsMasterKeyOnceRegardlessOfFileCount()
        {
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(_source.ConnectionString);
            await scenario.SetMasterKeyAsync("archive-backup-key");
            const int fileCount = 32;
            for (int index = 0; index < fileCount; index++)
            {
                await scenario.WriteSourceFileAsync($"file-{index}.txt", []);
            }
            await scenario.RunBackupAsync();
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            Guid owner = await scenario.Database.Modules.Where(x => x.Id == scenario.StorageModuleId).Select(x => x.UserId).SingleAsync();
            CountingStreamCipher cipher = new(scenario.Cipher);
            IBackupProvider provider = new FileSystemBackupSource(NullLogger<FileSystemBackupSource>.Instance);
            SnapshotController controller = new(cipher, scenario.Database, NullLogger<SnapshotController>.Instance, [provider], new())
            {
                ControllerContext = new()
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new(new ClaimsIdentity([new Claim("sub", owner.ToString())], "Test")),
                    },
                },
            };
            using MemoryStream archive = new();
            controller.Response.Body = archive;

            await controller.DownloadSnapshotArchive(snapshot.Id, default);

            Assert.That(cipher.Decryptions, Is.EqualTo(2), "One module credential decryption and one backup key decryption are sufficient.");
            archive.Position = 0;
            using ZipArchive zip = new(archive, ZipArchiveMode.Read);
            Assert.That(zip.Entries, Has.Count.EqualTo(fileCount));
        }
    }
}
