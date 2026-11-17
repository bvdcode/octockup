// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Models.Enums;
using Octockup.Server.Modules;
using System.Text;
using System.Text.Json;

namespace Octockup.Tests
{
    [Category("Integration")]
    public class YandexMusicBackupTests
    {
        private PostgresTestDatabase _database = null!;

        [OneTimeSetUp]
        public async Task CreateDatabaseAsync() => _database = await PostgresTestDatabase.CreateAsync();

        [OneTimeTearDown]
        public async Task DropDatabaseAsync() => await _database.DisposeAsync();

        [Test]
        public async Task Backup_RestoresPlaylistAudioAndLyricsAndReusesUnchangedTracks()
        {
            using YandexMusicTestServer server = new();
            await using BackupIntegrationScenario scenario = await BackupIntegrationScenario.CreateAsync(
                _database.ConnectionString, configureServices: services =>
                {
                    services.AddSingleton<IHttpClientFactory>(server);
                    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(server.Options));
                    services.AddTransient<IBackupProvider, YandexMusicSource>();
                });
            Backup backup = await scenario.Database.Backups.Include(x => x.Source).SingleAsync(x => x.Id == scenario.BackupId);
            backup.Source.BackupModuleId = typeof(YandexMusicSource).FullName!;
            backup.Source.Params(scenario.Cipher)["playlistUrl"] = YandexMusicTestServer.PlaylistUrl;
            backup.Source.Params(scenario.Cipher)["token"] = YandexMusicTestServer.Token;
            await scenario.Database.SaveChangesAsync();

            Schedule first = await scenario.RunBackupAsync();
            Assert.That(first.Status, Is.EqualTo(ScheduleStatus.Completed), first.ErrorMessage);
            Snapshot snapshot = await scenario.GetLatestSnapshotAsync();
            List<SnapshotFile> files = await scenario.Database.SnapshotFiles.Where(x => x.SnapshotId == snapshot.Id).ToListAsync();
            SnapshotFile audio = files.First(x => x.Path.EndsWith(".flac", StringComparison.Ordinal));
            byte[] restoredAudio = await scenario.RestoreFileAsync(snapshot.Id, audio.Path);
            byte[] restoredLyrics = await scenario.RestoreFileAsync(snapshot.Id, Path.ChangeExtension(audio.Path, ".lrc"));
            byte[] originalPlaylist = await scenario.RestoreFileAsync(snapshot.Id, "playlist.m3u8");
            byte[] manifest = await scenario.RestoreFileAsync(snapshot.Id, "playlist.json");
            using JsonDocument document = JsonDocument.Parse(manifest);
            Assert.Multiple(() =>
            {
                Assert.That(files, Has.Count.EqualTo(6));
                Assert.That(audio.Size, Is.EqualTo(restoredAudio.LongLength));
                Assert.That(Encoding.ASCII.GetString(restoredAudio, 0, 4), Is.EqualTo("fLaC"));
                Assert.That(Encoding.UTF8.GetString(restoredLyrics), Does.StartWith("[00:01.00]"));
                Assert.That(document.RootElement.GetProperty("tracks").GetArrayLength(), Is.EqualTo(4));
                Assert.That(server.AudioDownloads, Is.EqualTo(2));
            });

            server.ReversePlaylist = true;
            Schedule next = await scenario.RunBackupAsync();
            Assert.That(next.Status, Is.EqualTo(ScheduleStatus.Completed), next.ErrorMessage);
            Snapshot nextSnapshot = await scenario.GetLatestSnapshotAsync();
            byte[] changedPlaylist = await scenario.RestoreFileAsync(nextSnapshot.Id, "playlist.m3u8");
            Assert.Multiple(() =>
            {
                Assert.That(changedPlaylist, Is.Not.EqualTo(originalPlaylist));
                Assert.That(server.AudioDownloads, Is.EqualTo(2), "Unchanged tracks must not be downloaded again.");
            });
        }
    }
}
