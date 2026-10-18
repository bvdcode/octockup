// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.EntityFrameworkCore.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Octockup.Server.Database
{
    [Table("snapshots")]
    public class Snapshot : BaseEntity<Guid>, IImportableEntity
    {
        [Column("backup_id")]
        public Guid BackupId { get; set; }

        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        [Column("total_size")]
        public long TotalSize { get; set; }

        [Column("files_count")]
        public int FilesCount { get; set; }

        [Column("deletion_requested_at")]
        public DateTime? DeletionRequestedAt { get; set; }

        [Column("deletion_error")]
        public string? DeletionError { get; set; }

        [DeleteBehavior(DeleteBehavior.Restrict)]
        public virtual Backup Backup { get; set; } = null!;

        [DeleteBehavior(DeleteBehavior.Restrict)]
        public virtual Schedule? Schedule { get; set; }

        public virtual ICollection<SnapshotFile> Files { get; set; } = [];

        void IImportableEntity.RestoreId(Guid id)
        {
            Id = id;
        }
    }
}
