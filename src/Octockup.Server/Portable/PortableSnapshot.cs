// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Portable
{
    public record PortableSnapshot(Guid Id, Guid BackupId, Guid Generation, string Tag,
        DateTime CreatedAt, DateTime CompletedAt, int FilesCount, long TotalSize);
}
