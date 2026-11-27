// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Models
{
    internal record OidcIdentityClaims(
        string Subject,
        string? Email,
        string? DisplayName,
        string? AvatarUrl);
}
