// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Octockup.Server.Services;

namespace Octockup.Server.Controllers
{
    public partial class SnapshotController
    {
        private async Task<StorageOperationLease?> AcquireReadAsync(
            Guid snapshotId, Guid userId, CancellationToken cancellationToken)
        {
            Guid? backupId = await _dbContext.Snapshots.Where(x => x.Id == snapshotId &&
                x.Backup.Source.UserId == userId && x.DeletionRequestedAt == null && x.Backup.DeletionRequestedAt == null)
                .Select(x => (Guid?)x.BackupId).SingleOrDefaultAsync(cancellationToken);
            if (backupId is null)
            {
                return null;
            }
            StorageOperationLease lease = await operations.AcquireSnapshotUseAsync(backupId.Value, cancellationToken);
            bool available = false;
            try
            {
                available = await _dbContext.Snapshots.AnyAsync(x => x.Id == snapshotId &&
                    x.DeletionRequestedAt == null && x.Backup.DeletionRequestedAt == null, cancellationToken);
                if (available)
                {
                    return lease;
                }
            }
            finally
            {
                if (!available)
                {
                    await lease.DisposeAsync();
                }
            }
            return null;
        }
    }
}
