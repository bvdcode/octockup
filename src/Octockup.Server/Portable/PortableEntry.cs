// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Helpers;
namespace Octockup.Server.Portable
{
    public record PortableEntry(string Path, string Name, bool IsDirectory, long Size,
        DateTime? LastModified, string? Hashsum, List<ChunkStorageDescriptor> Chunks);
}
