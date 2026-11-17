// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Models;
using Octockup.Server.Models.YandexMusic;
using System.Text;
using System.Text.Json;

namespace Octockup.Server.Modules
{
    public partial class YandexMusicSource
    {
        public async Task<Stream> GetFileStreamAsync(BackupFileInfo file, CancellationToken cancellationToken = default)
        {
            if (_documents.TryGetValue(file.Path, out byte[]? document))
            {
                await SaveStateAsync(file.Path, DocumentVersion(document), file, Path.GetExtension(file.Path),
                    document.LongLength, cancellationToken);
                return new MemoryStream(document, writable: false);
            }
            if (!_files.TryGetValue(file.Path, out YandexMusicTrack? track))
            {
                throw new FileNotFoundException("Music file is not in the current playlist.", file.Path);
            }
            if (Path.GetExtension(file.Path) == ".lrc")
            {
                byte[] lyrics = await _client.GetLyricsAsync(track.Id, "LRC", cancellationToken);
                await SaveStateAsync(track.Id + ".lrc", track.Version, file, ".lrc", lyrics.LongLength, cancellationToken);
                return new MemoryStream(lyrics, writable: false);
            }
            await _downloads.WaitAsync(cancellationToken);
            string? temporaryPath = null;
            try
            {
                if (_downloadedTrack)
                {
                    await Task.Delay(options.Value.TrackDelay, cancellationToken);
                }
                YandexMusicDownload download = await _client.GetDownloadAsync(track.Id, cancellationToken);
                if (download.Extension != Path.GetExtension(file.Path))
                {
                    await SaveStateAsync(track.Id, string.Empty, file, download.Extension, 0, cancellationToken);
                    throw new InvalidDataException("The audio format changed during backup. Restart the backup to refresh its file list.");
                }
                temporaryPath = Path.Combine(Path.GetTempPath(), $"octockup-music-{Guid.NewGuid():N}{download.Extension}");
                await _client.DownloadAsync(download.Url, temporaryPath, cancellationToken);
                byte[]? cover = null;
                if (track.CoverUri is not null)
                {
                    Uri coverUrl = new("https://" + track.CoverUri.Replace("%%", "orig", StringComparison.Ordinal));
                    cover = await _client.GetBytesAsync(coverUrl, cancellationToken);
                }
                string? lyrics = null;
                if (!track.HasLrc && track.HasTextLyrics)
                {
                    lyrics = Encoding.UTF8.GetString(await _client.GetLyricsAsync(track.Id, "TEXT", cancellationToken));
                }
                cancellationToken.ThrowIfCancellationRequested();
                WriteTags(temporaryPath, track, cover, lyrics);
                long size = new FileInfo(temporaryPath).Length;
                await SaveStateAsync(track.Id, track.Version, file, download.Extension, size, cancellationToken);
                FileStream stream = new(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                temporaryPath = null;
                _downloadedTrack = true;
                return stream;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to download Yandex Music track {TrackId}.", track.Id);
                throw;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    File.Delete(temporaryPath);
                }
                _downloads.Release();
            }
        }

        private async Task SaveStateAsync(string key, string version, BackupFileInfo file,
            string extension, long size, CancellationToken cancellationToken)
        {
            DateTime modified = DateTime.UtcNow;
            YandexMusicFileState state = new(version, extension, size, modified);
            await _stateWrites.WaitAsync(cancellationToken);
            string temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _state[key] = state;
                Directory.CreateDirectory(options.Value.CacheDirectory);
                await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 16 * 1024, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(output, _state, cancellationToken: cancellationToken);
                }
                File.Move(temporary, _cachePath!, overwrite: true);
                file.Size = size;
                file.LastModified = modified;
            }
            finally
            {
                File.Delete(temporary);
                _stateWrites.Release();
            }
        }

        private static void WriteTags(string path, YandexMusicTrack track, byte[]? cover, string? lyrics)
        {
            using TagLib.File audio = TagLib.File.Create(path);
            audio.Tag.Title = track.Title;
            audio.Tag.Performers = track.Artists;
            audio.Tag.Album = track.Album;
            audio.Tag.AlbumArtists = track.AlbumArtists;
            audio.Tag.Year = track.Year;
            audio.Tag.Track = track.Number;
            audio.Tag.Disc = track.Disc;
            audio.Tag.Genres = track.Genre is null ? [] : [track.Genre];
            audio.Tag.Comment = $"https://music.yandex.ru/track/{track.Id}";
            audio.Tag.Lyrics = lyrics;
            if (cover is not null)
            {
                audio.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover))
                {
                    Type = TagLib.PictureType.FrontCover,
                }];
            }
            audio.Save();
        }
    }
}
