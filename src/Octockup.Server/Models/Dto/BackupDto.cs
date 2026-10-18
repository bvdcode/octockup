// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Dto;

namespace Octockup.Server.Models.Dto
{
    public class BackupDto : BaseDto<Guid>
    {
        public Guid SourceId { get; set; }
        public Guid StorageId { get; set; }
        public string Tag { get; set; } = string.Empty;
        public ICollection<string> IgnoredPaths { get; set; } = [];
        public bool DisableCompression { get; set; }
        public bool DisableEncryption { get; set; }
        public DateTime? DeletionRequestedAt { get; set; }
        public string? DeletionError { get; set; }
        public ModuleDto Source { get; set; } = null!;
        public ModuleDto Storage { get; set; } = null!;
        public ICollection<SnapshotDto> Snapshots { get; set; } = [];
        public ICollection<ScheduleDto> Schedules { get; set; } = [];
    }
}
