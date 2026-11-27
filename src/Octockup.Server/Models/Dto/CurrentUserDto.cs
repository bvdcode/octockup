// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Text.Json.Serialization;

namespace Octockup.Server.Models.Dto
{
    public class CurrentUserDto : UserDto
    {
        public string DisplayName { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AvatarUrl { get; set; }

        public bool IsAdmin { get; set; }
        public bool IsDisabled { get; set; }
        public int ExternalIdentityCount { get; set; }
    }
}
