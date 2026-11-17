// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Options;
using Octockup.Server.Abstractions;
using Octockup.Server.Helpers;
using Octockup.Server.Models;
using Octockup.Server.Models.YandexMusic;
using Octockup.Server.Services;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Octockup.Server.Modules
{
    public partial class YandexMusicSource(
        IHttpClientFactory httpClientFactory,
        IOptions<YandexMusicOptions> options,
        ILogger<YandexMusicSource> logger) : IBackupSource
    {
        private const string ManifestPath = "playlist.json";
        private const string PlaylistPath = "playlist.m3u8";
        private const int TrackBatchSize = 100;
        private readonly YandexMusicClient _client = new(
            httpClientFactory.CreateClient(nameof(YandexMusicSource)), options.Value, logger);
        private readonly SemaphoreSlim _initialization = new(1, 1);
        private readonly SemaphoreSlim _downloads = new(1, 1);
        private readonly SemaphoreSlim _stateWrites = new(1, 1);
        private readonly ConcurrentDictionary<string, YandexMusicTrack> _files = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, YandexMusicFileState> _state = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _documents = new(StringComparer.Ordinal);
        private List<YandexMusicTrack>? _tracks;
        private ICollection<string> _ignoredPaths = [];
        private string? _apiPath;
        private string? _cachePath;
        private bool _downloadedTrack;

        public string Id => typeof(YandexMusicSource).FullName!;
        public string Name => "Yandex Music";
        public char PathSeparator => '/';
        public IEnumerable<string> RequiredParameters => ["playlistUrl", "token"];

        public void SetParameters(IReadOnlyDictionary<string, string> parameters)
        {
            string token = parameters["token"].Trim();
            if (string.IsNullOrEmpty(token) || token.Contains('\r') || token.Contains('\n'))
            {
                throw new ArgumentException("A Yandex Music token is required.");
            }
            string url = parameters["playlistUrl"].Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? playlist) || playlist.Scheme != Uri.UriSchemeHttps
                || playlist.Host != "music.yandex.ru" || !string.IsNullOrEmpty(playlist.UserInfo) || !playlist.IsDefaultPort)
            {
                throw new ArgumentException("Expected a https://music.yandex.ru/users/<user>/playlists/<id> URL.");
            }
            string[] segments = playlist.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 4 || segments[0] != "users" || segments[2] != "playlists"
                || string.IsNullOrEmpty(segments[1]) || !long.TryParse(segments[3], out long kind) || kind <= 0)
            {
                throw new ArgumentException("Expected a Yandex Music user playlist URL.");
            }
            _apiPath = $"users/{Uri.EscapeDataString(Uri.UnescapeDataString(segments[1]))}/playlists/{kind}";
            string cacheId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_apiPath)));
            _cachePath = Path.Combine(options.Value.CacheDirectory, cacheId + ".json");
            _client.SetToken(token);
            _tracks = null;
            _files.Clear();
            _state.Clear();
            _documents.Clear();
            _downloadedTrack = false;
        }

        public void SetIgnoredPaths(ICollection<string>? ignoredPaths) => _ignoredPaths = ignoredPaths ?? [];

        public IEnumerable<BackupFileInfo> GetFiles(bool recursive = false, CancellationToken cancellationToken = default)
        {
            EnsureInitializedAsync(cancellationToken).GetAwaiter().GetResult();
            if (!IsIgnored(ManifestPath))
            {
                yield return DocumentInfo(ManifestPath);
            }
            List<string> playlist = ["#EXTM3U"];
            HashSet<string> emitted = new(StringComparer.Ordinal);
            foreach (YandexMusicTrack track in _tracks!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!track.Available)
                {
                    logger.LogWarning("Yandex Music track {TrackId} is unavailable; its metadata remains in the playlist manifest.", track.Id);
                    continue;
                }
                if (!recursive || ScheduleHelpers.IsDirectoryIgnored(
                    track.BasePath[..track.BasePath.LastIndexOf('/')], _ignoredPaths, PathSeparator))
                {
                    continue;
                }
                string stateKey = track.Id;
                _state.TryGetValue(stateKey, out YandexMusicFileState? state);
                if (state?.Version != track.Version)
                {
                    state = null;
                }
                string extension = state?.Extension ?? _client.GetDownloadAsync(track.Id, cancellationToken).GetAwaiter().GetResult().Extension;
                string audioPath = track.BasePath + extension;
                if (!IsIgnored(audioPath))
                {
                    playlist.Add($"#EXTINF:-1,{OneLine(string.Join(", ", track.Artists))} - {OneLine(track.Title)}");
                    playlist.Add(audioPath);
                    if (emitted.Add(audioPath))
                    {
                        _files[audioPath] = track;
                        yield return TrackInfo(audioPath, state);
                    }
                }
                string lrcPath = track.BasePath + ".lrc";
                if (track.HasLrc && !IsIgnored(lrcPath) && emitted.Add(lrcPath))
                {
                    _files[lrcPath] = track;
                    _state.TryGetValue(track.Id + ".lrc", out YandexMusicFileState? lyricsState);
                    yield return TrackInfo(lrcPath, lyricsState?.Version == track.Version ? lyricsState : null);
                }
            }
            _documents[PlaylistPath] = Encoding.UTF8.GetBytes(string.Join('\n', playlist) + "\n");
            if (!IsIgnored(PlaylistPath))
            {
                yield return DocumentInfo(PlaylistPath);
            }
        }

        public IEnumerable<string> GetDirectories(bool recursive = false, CancellationToken cancellationToken = default)
        {
            EnsureInitializedAsync(cancellationToken).GetAwaiter().GetResult();
            return _tracks!.Where(x => x.Available)
                .Select(x => recursive ? x.BasePath[..x.BasePath.LastIndexOf('/')] : x.BasePath[..x.BasePath.IndexOf('/')])
                .Distinct(StringComparer.Ordinal)
                .Where(x => !ScheduleHelpers.IsDirectoryIgnored(x, _ignoredPaths, PathSeparator))
                .ToArray();
        }

        public async Task<BackupFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken);
            BackupFileInfo? file = GetFiles(recursive: true, cancellationToken).FirstOrDefault(x => x.Path == path);
            return file;
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            await _initialization.WaitAsync(cancellationToken);
            try
            {
                if (_tracks is not null)
                {
                    return;
                }
                string apiPath = _apiPath ?? throw new InvalidOperationException("Yandex Music is not configured.");
                await _client.GetAsync("account/status", cancellationToken);
                JsonElement playlist = await _client.GetAsync(apiPath, cancellationToken);
                JsonElement[] entries = playlist.GetProperty("tracks").EnumerateArray().ToArray();
                Dictionary<string, YandexMusicTrack> tracks = new(StringComparer.Ordinal);
                foreach (JsonElement[] batch in entries.Chunk(TrackBatchSize))
                {
                    string ids = string.Join(',', batch.Select(x => YandexMusicTrack.ReadId(x.GetProperty("id"))));
                    JsonElement result = await _client.GetAsync("tracks", cancellationToken, form: new Dictionary<string, string>
                    {
                        ["track-ids"] = ids,
                        ["with-positions"] = "true",
                    });
                    foreach (JsonElement metadata in result.EnumerateArray())
                    {
                        YandexMusicTrack track = new(metadata);
                        tracks[track.Id] = track;
                    }
                }
                List<YandexMusicTrack> ordered = entries.Select(x => tracks[YandexMusicTrack.ReadId(x.GetProperty("id"))]).ToList();
                _documents[ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    playlist,
                    tracks = ordered.Select(x => x.Metadata),
                });
                if (File.Exists(_cachePath))
                {
                    await using FileStream input = File.OpenRead(_cachePath);
                    Dictionary<string, YandexMusicFileState> saved = await JsonSerializer.DeserializeAsync<Dictionary<string, YandexMusicFileState>>(
                        input, cancellationToken: cancellationToken) ?? throw new InvalidDataException("Invalid music metadata cache.");
                    foreach (KeyValuePair<string, YandexMusicFileState> entry in saved)
                    {
                        _state[entry.Key] = entry.Value;
                    }
                }
                _tracks = ordered;
            }
            finally
            {
                _initialization.Release();
            }
        }

        private bool IsIgnored(string path) => ScheduleHelpers.IsPathIgnored("/" + path, Path.GetFileName(path), _ignoredPaths);

        private BackupFileInfo DocumentInfo(string path)
        {
            byte[] content = _documents[path];
            _state.TryGetValue(path, out YandexMusicFileState? state);
            return TrackInfo(path, state?.Version == DocumentVersion(content) ? state : null);
        }

        private static string DocumentVersion(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

        private static BackupFileInfo TrackInfo(string path, YandexMusicFileState? state) => new()
        {
            Path = path,
            Name = Path.GetFileName(path),
            Size = state?.Size,
            LastModified = state?.LastModified,
        };

        private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
