// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

namespace Octockup.Server.Models.Requests
{
    public class CreateBackupRequest
    {
        public Guid SourceId { get; set; }
        public Guid StorageId { get; set; }
        public string Tag { get; set; } = string.Empty;
        public ICollection<string>? IgnoredPaths { get; set; }
        public bool DisableCompression { get; set; }
        public bool DisableEncryption { get; set; }
        public string? MasterKey { get; set; }
    }
}
