// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Dto;

namespace Octockup.Server.Models.Dto
{
    public class ModuleParametersDto : BaseDto<Guid>
    {
        public Dictionary<string, string> Parameters { get; set; } = [];
        public string[] StoredSecrets { get; set; } = [];
    }
}
