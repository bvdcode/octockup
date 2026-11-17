// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Octockup.Server.Models.YandexMusic;
using Octockup.Server.Modules;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Octockup.Tests
{
    internal class YandexMusicTestServer : HttpMessageHandler, IHttpClientFactory
    {
        public const string Token = "test-music-token";
        public const string PlaylistUrl = "https://music.yandex.ru/users/library/playlists/3";
        public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "music-test-" + Guid.NewGuid().ToString("N"));
        public int AudioDownloads { get; private set; }
        public int AudioRequests { get; private set; }
        public int FileInfoRequests { get; private set; }
        public int AccountRequests { get; private set; }
        public bool RejectToken { get; set; }
        public int TransientFailures { get; set; }
        public bool FailAudio { get; set; }
        public bool ReversePlaylist { get; set; }
        public bool ChangeTitle { get; set; }
        public List<string> RequestedTrackBatches { get; } = [];

        public YandexMusicOptions Options => new()
        {
            CacheDirectory = CacheDirectory,
            TrackDelay = TimeSpan.Zero,
            RetryDelay = TimeSpan.Zero,
        };

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        public YandexMusicSource CreateSource()
        {
            YandexMusicSource source = new(this, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<YandexMusicSource>.Instance);
            source.SetParameters(new Dictionary<string, string> { ["playlistUrl"] = PlaylistUrl, ["token"] = Token });
            return source;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri!;
            if (uri.Host != "api.music.yandex.net")
            {
                Assert.That(request.Headers.Authorization, Is.Null, "The token must not be sent to media hosts.");
                if (uri.AbsolutePath == "/cover/orig")
                {
                    return Bytes(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jF2kAAAAASUVORK5CYII="));
                }
                if (uri.AbsolutePath == "/lyrics")
                {
                    return Bytes(Encoding.UTF8.GetBytes("[00:01.00]test lyrics\n"));
                }
                AudioRequests++;
                if (FailAudio)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                AudioDownloads++;
                return Bytes(Flac());
            }
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("OAuth"));
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo(Token));
            if (RejectToken)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (TransientFailures > 0)
            {
                TransientFailures--;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            switch (uri.AbsolutePath)
            {
                case "/account/status":
                    AccountRequests++;
                    return Json(new { account = new { uid = 42 } });
                case "/users/library/playlists/3":
                    int[] ids = ReversePlaylist ? [1, 2, 2, 3] : [2, 1, 2, 3];
                    return Json(new { title = "Library", revision = ReversePlaylist ? 2 : 1, trackCount = 4,
                        tracks = ids.Select(x => new { id = x }) });
                case "/tracks":
                    Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
                    string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    RequestedTrackBatches.Add(body);
                    return Json(new[] { Track(1), Track(2), Track(3) });
                case "/get-file-info":
                    FileInfoRequests++;
                    Assert.That(request.Headers.GetValues("X-Yandex-Music-Client").Single(), Is.EqualTo("YandexMusicDesktopAppWindows/5.95.0"));
                    Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(uri.Query);
                    string payload = $"{query["ts"]}{query["trackId"]}lossless{query["codecs"].ToString().Replace(",", string.Empty)}raw";
                    string signature = Convert.ToBase64String(HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes("kzqU4XhfCaY6B6JTHODeq5"), Encoding.UTF8.GetBytes(payload))).TrimEnd('=');
                    Assert.That(query["sign"].ToString(), Is.EqualTo(signature));
                    return Json(new { downloadInfo = new { codec = "flac", urls = new[] { "https://media.example.com/audio" } } });
                case "/tracks/1/lyrics":
                case "/tracks/2/lyrics":
                    return Json(new { downloadUrl = "https://media.example.com/lyrics" });
                default:
                    throw new InvalidOperationException("Unexpected music request: " + uri.AbsolutePath);
            }
        }

        private object Track(int id) => new
        {
            id,
            title = ChangeTitle && id == 1 ? "Renamed" : "A track",
            available = id != 3,
            artists = new[] { new { name = "Artist", id = 10 } },
            albums = new[] { new { id = 20, title = "Album", year = 2025, genre = "rock",
                artists = new[] { new { name = "Artist", id = 10 } }, trackPosition = new { index = 1, volume = 1 } } },
            coverUri = "media.example.com/cover/%%",
            lyricsInfo = new { hasAvailableSyncLyrics = true, hasAvailableTextLyrics = true },
        };

        private static HttpResponseMessage Json<T>(T result) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { result }),
        };

        private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

        private static byte[] Flac()
        {
            byte[] file = new byte[42];
            Encoding.ASCII.GetBytes("fLaC").CopyTo(file, 0);
            file[4] = 128;
            file[7] = 34;
            file[8] = 16;
            file[10] = 16;
            return file;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }
            base.Dispose(disposing);
        }
    }
}
