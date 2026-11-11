// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Portable
{
    public record PortablePage(List<PortableEntry> Entries, int Page, int Pages, long Total);
}
