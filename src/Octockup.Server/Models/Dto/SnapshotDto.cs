// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Dto;

namespace Octockup.Server.Models.Dto
{
    public class SnapshotDto : BaseDto<Guid>
    {
        public Guid BackupId { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? DeletionRequestedAt { get; set; }
        public string? DeletionError { get; set; }
        public int FilesCount { get; internal set; }
        public long TotalSize { get; internal set; }
    }
}
