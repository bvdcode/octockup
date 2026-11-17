// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Models;
using Octockup.Server.Modules;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Octockup.Tests
{
    public class YandexMusicSourceTests
    {
        [TestCase("https://example.com/users/library/playlists/3")]
        [TestCase("http://music.yandex.ru/users/library/playlists/3")]
        [TestCase("https://music.yandex.ru/artist/42")]
        [TestCase("https://music.yandex.ru/users/library/playlists/0")]
        public void SetParameters_RejectsInvalidPlaylistUrl(string url)
        {
            using YandexMusicTestServer server = new();
            YandexMusicSource source = server.CreateSource();
            Assert.Throws<ArgumentException>(() => source.SetParameters(new Dictionary<string, string>
            {
                ["playlistUrl"] = url, ["token"] = YandexMusicTestServer.Token,
            }));
        }

        [Test]
        public async Task GetFiles_PreservesOrderDuplicatesAndUnavailableTracksInManifest()
        {
            using YandexMusicTestServer server = new();
            YandexMusicSource source = server.CreateSource();
            List<BackupFileInfo> files = source.GetFiles(recursive: true).ToList();
            string json = await ReadAsync(source, files.Single(x => x.Path == "playlist.json"));
            string m3u = await ReadAsync(source, files.Single(x => x.Path == "playlist.m3u8"));
            using JsonDocument manifest = JsonDocument.Parse(json);
            string[] audioPaths = m3u.Split('\n').Where(x => x.EndsWith(".flac", StringComparison.Ordinal)).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(source.RequiredParameters, Is.EqualTo(new[] { "playlistUrl", "token" }));
                Assert.That(files.Count(x => x.Path.EndsWith(".flac", StringComparison.Ordinal)), Is.EqualTo(2));
                Assert.That(files.Count(x => x.Path.EndsWith(".lrc", StringComparison.Ordinal)), Is.EqualTo(2));
                Assert.That(audioPaths, Has.Length.EqualTo(3));
                Assert.That(audioPaths[0], Does.EndWith("[2].flac"));
                Assert.That(audioPaths[1], Does.EndWith("[1].flac"));
                Assert.That(audioPaths[2], Is.EqualTo(audioPaths[0]));
                Assert.That(manifest.RootElement.GetProperty("tracks").GetArrayLength(), Is.EqualTo(4));
                Assert.That(json, Does.Not.Contain(YandexMusicTestServer.Token));
                Assert.That(server.AudioDownloads, Is.Zero);
            });
        }

        [Test]
        public async Task GetFileStream_DownloadsTaggedAudioAndCachesOnlyMetadata()
        {
            using YandexMusicTestServer server = new();
            YandexMusicSource source = server.CreateSource();
            BackupFileInfo audio = source.GetFiles(recursive: true).First(x => x.Path.EndsWith(".flac", StringComparison.Ordinal));
            string path;
            await using (FileStream stream = (FileStream)await source.GetFileStreamAsync(audio))
            {
                path = stream.Name;
                using TagLib.File tagged = TagLib.File.Create(path);
                Assert.Multiple(() =>
                {
                    Assert.That(audio.Size, Is.EqualTo(stream.Length));
                    Assert.That(tagged.Tag.Title, Is.EqualTo("A track"));
                    Assert.That(tagged.Tag.Album, Is.EqualTo("Album"));
                    Assert.That(tagged.Tag.Performers, Is.EqualTo(new[] { "Artist" }));
                    Assert.That(tagged.Tag.Pictures, Has.Length.EqualTo(1));
                });
            }
            Assert.That(File.Exists(path), Is.False);
            YandexMusicSource nextSource = server.CreateSource();
            BackupFileInfo next = nextSource.GetFiles(recursive: true).Single(x => x.Path == audio.Path);
            Assert.Multiple(() =>
            {
                Assert.That(next.Size, Is.EqualTo(audio.Size));
                Assert.That(next.LastModified, Is.EqualTo(audio.LastModified));
                Assert.That(server.AudioDownloads, Is.EqualTo(1));
                Assert.That(Directory.GetFiles(server.CacheDirectory).All(x => Path.GetExtension(x) == ".json"), Is.True);
            });
            string cache = await File.ReadAllTextAsync(Directory.GetFiles(server.CacheDirectory).Single());
            Assert.That(cache, Does.Not.Contain(YandexMusicTestServer.Token));
        }

        [Test]
        public void GetFiles_IgnoresAlbumsBeforeLookingUpDownloads()
        {
            using YandexMusicTestServer server = new();
            YandexMusicSource source = server.CreateSource();
            source.SetIgnoredPaths(["/Artist/Album"]);
            List<BackupFileInfo> files = source.GetFiles(recursive: true).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(files.Select(x => x.Path), Is.EqualTo(new[] { "playlist.json", "playlist.m3u8" }));
                Assert.That(server.FileInfoRequests, Is.Zero);
            });
        }

        [Test]
        public void GetFiles_InvalidTokenFailsWithoutRetryOrLeakingCredentials()
        {
            using YandexMusicTestServer server = new() { RejectToken = true };
            YandexMusicSource source = server.CreateSource();
            HttpRequestException? exception = Assert.Throws<HttpRequestException>(() => source.GetFiles(true).ToList());
            Assert.Multiple(() =>
            {
                Assert.That(exception?.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(exception?.Message, Does.Not.Contain(YandexMusicTestServer.Token));
            });
        }

        [Test]
        public void GetFiles_RetriesTransientResponsesAndHonorsCancellation()
        {
            using YandexMusicTestServer server = new() { TransientFailures = 2 };
            YandexMusicSource source = server.CreateSource();
            Assert.That(source.GetFiles(true).ToList(), Is.Not.Empty);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Catch<OperationCanceledException>(() => source.GetFiles(true, cancellation.Token).ToList());
            Assert.That(server.AccountRequests, Is.EqualTo(1));
        }

        [Test]
        public void GetFileStream_FailedTransferRetriesThreeTimesWithoutSavingFileMetadata()
        {
            using YandexMusicTestServer server = new() { FailAudio = true };
            YandexMusicSource source = server.CreateSource();
            BackupFileInfo audio = source.GetFiles(true).First(x => x.Path.EndsWith(".flac", StringComparison.Ordinal));

            Assert.ThrowsAsync<HttpRequestException>(() => source.GetFileStreamAsync(audio));

            Assert.Multiple(() =>
            {
                Assert.That(server.AudioRequests, Is.EqualTo(3));
                Assert.That(audio.Size, Is.Null);
                Assert.That(Directory.Exists(server.CacheDirectory), Is.False);
            });
        }

        [Test]
        public async Task GetFiles_ChangedMetadataInvalidatesAudioCache()
        {
            using YandexMusicTestServer server = new();
            YandexMusicSource source = server.CreateSource();
            BackupFileInfo audio = source.GetFiles(true).Single(x => x.Path.EndsWith("[1].flac", StringComparison.Ordinal));
            await using (Stream stream = await source.GetFileStreamAsync(audio))
            {
                Assert.That(stream.CanRead, Is.True);
            }
            server.ChangeTitle = true;
            YandexMusicSource changed = server.CreateSource();
            BackupFileInfo renamed = changed.GetFiles(true).Single(x => x.Path.EndsWith("[1].flac", StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(renamed.Path, Does.Contain("Renamed"));
                Assert.That(renamed.Size, Is.Null);
            });
        }

        private static async Task<string> ReadAsync(YandexMusicSource source, BackupFileInfo file)
        {
            await using Stream stream = await source.GetFileStreamAsync(file);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }
}
