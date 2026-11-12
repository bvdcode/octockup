// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Portable
{
    public record PortableDownloadRequest(string Session, Guid SnapshotId, string? Path, int Page, bool Folder);
}
