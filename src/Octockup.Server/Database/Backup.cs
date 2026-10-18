// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.EntityFrameworkCore.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Octockup.Server.Database
{
    [Table("backups")]
    [Index(nameof(Tag), IsUnique = true)]
    public class Backup : BaseEntity<Guid>, IImportableEntity
    {
        [Column("source_id")]
        public Guid SourceId { get; set; }

        [Column("storage_id")]
        public Guid StorageId { get; set; }

        [Column("tag")]
        public string Tag { get; set; } = null!;

        [Column("ignored_paths")]
        public ICollection<string> IgnoredPaths { get; set; } = [];

        [Column("disable_compression")]
        public bool DisableCompression { get; set; }

        [Column("disable_encryption")]
        public bool DisableEncryption { get; set; }

        [Column("deletion_requested_at")]
        public DateTime? DeletionRequestedAt { get; set; }

        [Column("deletion_error")]
        public string? DeletionError { get; set; }

        [DeleteBehavior(DeleteBehavior.Restrict)]
        public virtual Module Source { get; set; } = null!;

        [DeleteBehavior(DeleteBehavior.Restrict)]
        public virtual Module Storage { get; set; } = null!;

        public virtual ICollection<Snapshot> Snapshots { get; set; } = [];
        public virtual ICollection<Schedule> Schedules { get; set; } = [];

        void IImportableEntity.RestoreId(Guid id)
        {
            Id = id;
        }
    }
}
