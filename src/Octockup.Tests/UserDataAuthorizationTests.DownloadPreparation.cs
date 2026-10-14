// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octockup.Server.Controllers;
using Octockup.Server.Database;
using Octockup.Server.Helpers;

namespace Octockup.Tests
{
    public partial class UserDataAuthorizationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task SnapshotDownload_HttpResponsePreservesSizeAndDisablesTransformation(bool archive)
        {
            await using PostgresDbContext dbContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(dbContext, includeSnapshot: true);
            byte[] content = [1, 2, 3, 4, 5];
            await ConfigureDownloadAsync(dbContext, graph, content, content);
            TestStorage storage = new(graph.Storage.BackupModuleId, content);
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers();
            await using WebApplication app = builder.Build();
            app.MapGet("/download", async (HttpContext context) =>
            {
                SnapshotController controller = AsUser(
                    new SnapshotController(new PassThroughCipher(), dbContext,
                        NullLogger<SnapshotController>.Instance, [storage]),
                    graph.Source.UserId);
                context.User = controller.User;
                controller.ControllerContext.HttpContext = context;
                IActionResult result;
                if (archive)
                {
                    result = await controller.DownloadSnapshotArchive(graph.Snapshot!.Id, context.RequestAborted);
                }
                else
                {
                    result = await controller.DownloadSnapshotFile(graph.Snapshot!.Id, graph.SnapshotFile!.Id);
                }
                await result.ExecuteResultAsync(controller.ControllerContext);
            });
            await app.StartAsync();
            using HttpClient client = app.GetTestClient();
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("br, gzip, zstd");

            using HttpResponseMessage response = await client.GetAsync("/download", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long? declaredLength = response.Content.Headers.ContentLength;
            byte[] downloaded = await response.Content.ReadAsByteArrayAsync();

            Assert.Multiple(() =>
            {
                Assert.That(declaredLength, Is.EqualTo(downloaded.Length));
                Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "identity" }));
                Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
                Assert.That(response.Headers.CacheControl?.NoTransform, Is.True);
                Assert.That(response.Headers.TransferEncodingChunked, Is.Not.True);
            });
        }

        [Test]
        public async Task DownloadSnapshotArchive_LoadsChunkMetadataInBoundedBatches()
        {
            await using PostgresDbContext seedContext = CreateDbContext();
            OwnedGraph graph = await SeedOwnedGraphAsync(seedContext, includeSnapshot: true);
            const int chunkCount = 10_001;
            List<string> chunkKeys = Enumerable.Range(0, chunkCount)
                .Select(index => ChunkStorageHelpers.CreateKey(
                    index.ToString("x64"), CompressionAlgorithm.None, isEncrypted: false))
                .ToList();
            SnapshotFile file = graph.SnapshotFile!;
            file.Size = chunkCount;
            file.ChunkHashes = chunkKeys;
            seedContext.UploadedHashes.AddRange(chunkKeys.Select(key => new UploadedHash
            {
                ModuleId = graph.Storage.Id,
                Hash = key,
                StoredSize = 1,
                OriginalSize = 1,
                CompressionAlgorithm = CompressionAlgorithm.None,
            }));
            await seedContext.SaveChangesAsync();

            DownloadMetadataQueryInterceptor interceptor = new();
            DbContextOptions<PostgresDbContext> options = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using PostgresDbContext dbContext = new(options);
            TestStorage storage = new(graph.Storage.BackupModuleId, [42]);
            SnapshotController controller = AsUser(
                new SnapshotController(
                    new PassThroughCipher(), dbContext,
                    NullLogger<SnapshotController>.Instance, [storage]),
                graph.Source.UserId);
            using MemoryStream archive = new();
            controller.Response.Body = archive;

            await controller.DownloadSnapshotArchive(graph.Snapshot!.Id, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(interceptor.QueryCount, Is.EqualTo(2));
                Assert.That(interceptor.MaximumBatchSize, Is.LessThanOrEqualTo(10_000));
                Assert.That(storage.ReadCount, Is.EqualTo(chunkCount));
                Assert.That(controller.Response.ContentLength, Is.EqualTo(archive.Length));
            });
        }
    }
}
