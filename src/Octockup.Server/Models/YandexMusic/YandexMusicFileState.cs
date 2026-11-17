// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Models.YandexMusic
{
    public record YandexMusicFileState(string Version, string Extension, long Size, DateTime LastModified);
}
