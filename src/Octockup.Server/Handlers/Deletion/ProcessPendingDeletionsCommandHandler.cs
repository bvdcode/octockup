// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Octockup.Server.Database;
using Octockup.Server.Hubs;
using Octockup.Server.Jobs;
using Octockup.Server.Services;

namespace Octockup.Server.Handlers.Deletion
{
    public class ProcessPendingDeletionsCommandHandler(
        AppDbContext dbContext,
        StorageOperationCoordinator operations,
        IHubContext<EventHub> hub,
        ILogger<ProcessPendingDeletionsCommandHandler> logger)
        : IRequestHandler<ProcessPendingDeletionsCommand, int>
    {
        private const int FileBatchSize = 10_000;
        private const int BackupBatchSize = 100;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        public async Task<int> Handle(ProcessPendingDeletionsCommand request, CancellationToken cancellationToken)
        {
            DateTime retryBefore = DateTime.UtcNow - RetryDelay;
            List<PendingBackupDeletion> candidates = await dbContext.Backups.AsNoTracking()
                .Where(x => (x.DeletionRequestedAt != null &&
                    (x.DeletionError == null || x.UpdatedAt <= retryBefore)) ||
                    (x.DeletionRequestedAt == null && x.Snapshots.Any(s =>
                        s.DeletionRequestedAt != null && (s.DeletionError == null || s.UpdatedAt <= retryBefore))))
                .OrderBy(x => x.UpdatedAt).Take(BackupBatchSize)
                .Select(x => new PendingBackupDeletion(x.Id, x.Source.UserId, x.DeletionRequestedAt))
                .ToListAsync(cancellationToken);
            int completed = 0;
            foreach (PendingBackupDeletion backup in candidates)
            {
                if (backup.DeletionRequestedAt is not null)
                {
                    Guid[] schedules = await dbContext.Schedules.Where(x => x.BackupId == backup.Id)
                        .Select(x => x.Id).ToArrayAsync(cancellationToken);
                    foreach (Guid scheduleId in schedules)
                    {
                        ExecuteBackupJob.StopRunningBackup(scheduleId);
                    }
                }
                await using StorageOperationLease? lease = operations.TryAcquireSnapshotDeletion(backup.Id);
                if (lease is null)
                {
                    continue;
                }
                Guid? snapshotId = null;
                try
                {
                    IQueryable<Snapshot> pendingSnapshots = dbContext.Snapshots
                        .Where(x => x.BackupId == backup.Id && (backup.DeletionRequestedAt != null ||
                            (x.DeletionRequestedAt != null && (x.DeletionError == null || x.UpdatedAt <= retryBefore))));
                    while (true)
                    {
                        snapshotId = await pendingSnapshots.OrderBy(x => x.CreatedAt)
                            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
                        if (snapshotId is null)
                        {
                            break;
                        }
                        await DeleteSnapshotAsync(snapshotId.Value, cancellationToken);
                        completed++;
                        await NotifyAsync(backup.Id, backup.UserId, cancellationToken);
                    }
                    if (backup.DeletionRequestedAt is not null)
                    {
                        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                        await dbContext.Schedules.Where(x => x.BackupId == backup.Id).ExecuteDeleteAsync(cancellationToken);
                        await dbContext.Backups.Where(x => x.Id == backup.Id).ExecuteDeleteAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        completed++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Metadata deletion failed for backup {BackupId}, snapshot {SnapshotId}", backup.Id, snapshotId);
                    string error = exception.GetType().Name;
                    DateTime now = DateTime.UtcNow;
                    if (backup.DeletionRequestedAt is not null)
                    {
                        await dbContext.Backups.Where(x => x.Id == backup.Id).ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.DeletionError, error).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                    }
                    else
                    {
                        await dbContext.Snapshots.Where(x => x.Id == snapshotId).ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.DeletionError, error).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                    }
                }
                await NotifyAsync(backup.Id, backup.UserId, cancellationToken);
            }
            return completed;
        }

        private async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            string? cursor = null;
            while (true)
            {
                IQueryable<SnapshotFile> remaining = dbContext.SnapshotFiles.Where(x => x.SnapshotId == snapshotId);
                if (cursor is not null)
                {
                    remaining = remaining.Where(x => x.Path.CompareTo(cursor) > 0);
                }
                string? upperPath = await remaining.OrderBy(x => x.Path).Take(FileBatchSize)
                    .MaxAsync(x => (string?)x.Path, cancellationToken);
                if (upperPath is null)
                {
                    break;
                }
                await remaining.Where(x => x.Path.CompareTo(upperPath) <= 0).ExecuteDeleteAsync(cancellationToken);
                cursor = upperPath;
            }
#pragma warning disable EEX0009
            await dbContext.Database.ExecuteSqlRawAsync("ANALYZE snapshot_files", cancellationToken);
#pragma warning restore EEX0009
            await dbContext.Snapshots.Where(x => x.Id == snapshotId).ExecuteDeleteAsync(cancellationToken);
        }

        private Task NotifyAsync(Guid backupId, Guid userId, CancellationToken cancellationToken)
        {
            return hub.Clients.User(userId.ToString()).SendAsync("BackupDeletionChanged", backupId, cancellationToken);
        }
    }
}
