// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Octockup.Server.Models.YandexMusic
{
    public class YandexMusicTrack
    {
        public YandexMusicTrack(JsonElement metadata)
        {
            Metadata = metadata.Clone();
            Id = ReadId(metadata.GetProperty("id"));
            Title = FullTitle(metadata);
            Artists = Names(metadata, "artists");
            Available = metadata.GetProperty("available").GetBoolean();
            CoverUri = Text(metadata, "coverUri");
            JsonElement album = default;
            if (metadata.TryGetProperty("albums", out JsonElement albums) && albums.GetArrayLength() > 0)
            {
                album = albums[0];
            }
            Album = FullTitle(album);
            AlbumArtists = Names(album, "artists");
            Genre = Text(album, "genre");
            Year = ReadNumber(album, "year");
            if (album.ValueKind == JsonValueKind.Object && album.TryGetProperty("trackPosition", out JsonElement position))
            {
                Number = ReadNumber(position, "index");
                Disc = ReadNumber(position, "volume");
            }
            if (metadata.TryGetProperty("lyricsInfo", out JsonElement lyrics) && lyrics.ValueKind == JsonValueKind.Object)
            {
                HasLrc = lyrics.TryGetProperty("hasAvailableSyncLyrics", out JsonElement sync) && sync.GetBoolean();
                HasTextLyrics = lyrics.TryGetProperty("hasAvailableTextLyrics", out JsonElement text) && text.GetBoolean();
            }
            byte[] version = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Id, Title, Artists, Album, AlbumArtists, Genre, Year, Number, Disc, CoverUri, HasLrc, HasTextLyrics,
            });
            Version = Convert.ToHexStringLower(SHA256.HashData(version));
            string artist = AlbumArtists.FirstOrDefault() ?? string.Empty;
            string number = Disc > 1 ? $"{Disc}.{Number}" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            BasePath = $"{SafeName(artist)}/{SafeName(Album)}/{number} - {SafeName(Title)} [{SafeName(Id)}]";
        }

        public JsonElement Metadata { get; }
        public string Id { get; }
        public string Title { get; }
        public string[] Artists { get; }
        public string Album { get; }
        public string[] AlbumArtists { get; }
        public string? Genre { get; }
        public uint Year { get; }
        public uint Number { get; }
        public uint Disc { get; }
        public string? CoverUri { get; }
        public bool Available { get; }
        public bool HasLrc { get; }
        public bool HasTextLyrics { get; }
        public string Version { get; }
        public string BasePath { get; }

        public static string ReadId(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? throw new InvalidDataException("Empty music identifier."),
                JsonValueKind.Number => value.GetRawText(),
                _ => throw new InvalidDataException("Invalid music identifier."),
            };
        }

        private static string FullTitle(JsonElement value)
        {
            string title = Text(value, "title") ?? string.Empty;
            string? version = Text(value, "version");
            return string.IsNullOrWhiteSpace(version) ? title : $"{title} ({version})";
        }

        private static string? Text(JsonElement value, string name)
        {
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement text)
                && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }

        private static uint ReadNumber(JsonElement value, string name)
        {
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement number)
                && number.ValueKind == JsonValueKind.Number ? number.GetUInt32() : 0;
        }

        private static string[] Names(JsonElement value, string name)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement items))
            {
                return [];
            }
            return items.EnumerateArray().Select(x => Text(x, "name")).OfType<string>().ToArray();
        }

        private static string SafeName(string value)
        {
            string cleaned = Regex.Replace(value, @"[^\p{L}\p{N}\p{M}\-'() .]", "_").Trim(' ', '.');
            if (cleaned.Length == 0)
            {
                return "_";
            }
            return string.Concat(cleaned.EnumerateRunes().Take(40));
        }
    }
}
