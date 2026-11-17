// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Quartz.Attributes;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;
using Quartz;
using System.Diagnostics;

namespace Octockup.Server.Jobs
{
    [JobTrigger(minutes: 1)]
    public class StorageCleanupJob(
        AppDbContext dbContext,
        StorageCleanupProcessor processor,
        StorageOperationCoordinator storageOperations,
        ILogger<StorageCleanupJob> logger) : IJob
    {
        private static readonly DateTime ProcessStartedAt = GetProcessStartedAt();

        public Task Execute(IJobExecutionContext context)
        {
            return ExecuteAsync(context.CancellationToken);
        }

        internal async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            StorageCleanup? cleanup = await dbContext.StorageCleanups
                .Include(x => x.Module)
                .Include(x => x.LastRun)
                .Where(x => x.Status == StorageCleanupStatus.Running)
                .OrderBy(x => x.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (cleanup is null)
            {
                return;
            }

            StorageCleanupRun? run = cleanup.LastRun;
            if (run is not null && run.StartedAt < ProcessStartedAt)
            {
                await FailAsync(cleanup, run, "Cleanup was interrupted. Start it manually to continue.");
                logger.LogWarning("Storage cleanup {CleanupId} requires a manual restart.", cleanup.Id);
                return;
            }

            await using StorageOperationLease? lease = storageOperations.TryAcquireCleanup(cleanup.ModuleId);
            if (lease is null)
            {
                logger.LogDebug(
                    "Storage cleanup {CleanupId} is waiting for active backups on storage {StorageId}.",
                    cleanup.Id,
                    cleanup.ModuleId);
                return;
            }

            try
            {
                if (run is null || run.Status != StorageCleanupStatus.Running)
                {
                    throw new StorageCleanupConfigurationException(
                        $"Active cleanup run not found: {cleanup.Id}");
                }
                await processor.ProcessAsync(cleanup, run, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Storage cleanup {CleanupId} failed.", cleanup.Id);
                await FailAsync(cleanup, run, ex.Message);
                throw;
            }
        }

        private async Task FailAsync(StorageCleanup cleanup, StorageCleanupRun? run, string message)
        {
            cleanup.Status = StorageCleanupStatus.Failed;
            cleanup.ErrorMessage = message;
            if (run is not null)
            {
                run.Status = StorageCleanupStatus.Failed;
                run.CompletedAt = DateTime.UtcNow;
                run.ErrorMessage = message;
            }
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        private static DateTime GetProcessStartedAt()
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
    }
}
