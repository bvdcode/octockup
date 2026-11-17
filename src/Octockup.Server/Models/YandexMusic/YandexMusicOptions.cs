// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Models.YandexMusic
{
    public class YandexMusicOptions
    {
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan TrackDelay { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(60);
        public int Attempts { get; set; } = 3;
        public string CacheDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "music-cache");
    }
}
