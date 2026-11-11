// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Portable
{
    public record PortableIndex(int Version, List<Guid> Backups);
}
