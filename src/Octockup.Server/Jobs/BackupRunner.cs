// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Models.Enums;
using EasyExtensions.Streams;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Collections;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Hubs;
using Octockup.Server.Models;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Octockup.Server.Jobs
{
    public partial class BackupRunner(
        IStreamCipher crypto,
        AppDbContext dbContext,
        IServiceProvider serviceProvider,
        ILogger<BackupRunner> logger,
        IHubContext<EventHub> hubContext,
        IEnumerable<IBackupProvider> providers,
        StorageOperationCoordinator storageOperations)
    {
        private const int ChunkSize = 8 * 1024 * 1024;
        private const int PreviousFilesBatchSize = 4_096;
        private const int PrefetchedFileBatchCount = 2;
        private readonly List<UploadedHash> _pendingUploadedHashes = [];
        private readonly Stopwatch _uploadedHashesStopwatch = Stopwatch.StartNew();
        private const int UploadedHashesFlushCount = 500; // flush every 500 new hashes
        private static readonly TimeSpan UploadedHashesFlushInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SnapshotFlushInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PreviousFilesBatchIdleTimeout = TimeSpan.FromSeconds(1);

        public async Task RunAsync(Schedule schedule, CancellationToken cancellationToken)
        {
            Guid userId = schedule.Backup.Source.UserId;
            ScheduleReport report = new(userId, schedule.Id, schedule.BackupId, hubContext);
            report.StartBackgroundReporting(cancellationToken);
            StorageOperationLease? storageLease = null;
            StorageOperationLease? snapshotLease = null;

            try
            {
                snapshotLease = await storageOperations.AcquireSnapshotUseAsync(schedule.BackupId, cancellationToken);
                bool canRun = await dbContext.Schedules.AnyAsync(x => x.Id == schedule.Id &&
                    x.Backup.DeletionRequestedAt == null &&
                    !x.Backup.Snapshots.Any(s => s.DeletionRequestedAt != null), cancellationToken);
                if (!canRun)
                {
                    return;
                }
                IBackupSource? sourceProvider = CreateSourceProvider(schedule);
                if (sourceProvider is null)
                {
                    await report.SendAsync(0, schedule.ErrorMessage ?? "Source provider not found.", cancellationToken: cancellationToken);
                    return;
                }

                IBackupStorage? storageProvider = CreateStorageProvider(schedule);
                if (storageProvider is null)
                {
                    await report.SendAsync(0, schedule.ErrorMessage ?? "Storage provider not found.", cancellationToken: cancellationToken);
                    return;
                }

                storageLease = await storageOperations.AcquireBackupAsync(
                    schedule.Backup.StorageId,
                    cancellationToken);

                await report.SendAsync(0, "Listing files to backup...", cancellationToken: cancellationToken);
                schedule.Status = ScheduleStatus.Running;
                schedule.ErrorMessage = null;
                schedule.FinishedAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);

                // Set ignored paths before enumerating files
                sourceProvider.SetIgnoredPaths(schedule.Backup.IgnoredPaths);

                IEnumerable<BackupFileInfo> filesToBackup = sourceProvider.GetFiles(recursive: true, cancellationToken: cancellationToken);
                await BackupAsync(schedule, sourceProvider, storageProvider, report, filesToBackup, cancellationToken);

                schedule.Status = ScheduleStatus.Completed;
                schedule.FinishedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Schedule {ScheduleId} backup completed successfully", schedule.Id);
                await report.SendAsync(report.Processed, "Backup completed successfully.", status: ScheduleStatus.Completed, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                // Log the actual cancellation source for debugging
                logger.LogWarning(ex, "Schedule {ScheduleId} received OperationCanceledException. " +
                    "Token.IsCancellationRequested={IsCancellationRequested}, InnerException={InnerException}",
                    schedule.Id, cancellationToken.IsCancellationRequested, ex.InnerException?.Message);

                if (cancellationToken.IsCancellationRequested)
                {
                    await HandleCancellationAsync(schedule, report);
                }
                else
                {
                    // Cancellation from somewhere else (timeout, network issue, etc.)
                    schedule.ErrorMessage = $"Backup interrupted: {ex.Message}";
                    schedule.Status = ScheduleStatus.Failed;
                    schedule.FinishedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(CancellationToken.None);

                    logger.LogError(ex, "Schedule {ScheduleId} backup interrupted unexpectedly", schedule.Id);
                    await report.SendAsync(report.Processed, schedule.ErrorMessage, status: ScheduleStatus.Failed, cancellationToken: CancellationToken.None);
                }
                return;
            }
            catch (Exception ex)
            {
                schedule.ErrorMessage = $"Backup failed: {ex.Message}";
                schedule.Status = ScheduleStatus.Failed;
                schedule.FinishedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(CancellationToken.None);

                logger.LogError(ex, "Schedule {ScheduleId} backup failed", schedule.Id);
                await report.SendAsync(report.Processed, schedule.ErrorMessage, status: ScheduleStatus.Failed, cancellationToken: CancellationToken.None);
            }
            finally
            {
                try
                {
                    await FlushUploadedHashesAsync(CancellationToken.None);
                }
                catch (Exception flushEx)
                {
                    logger.LogError(flushEx, "Failed to flush pending uploaded hashes after backup execution");
                }

                if (storageLease is not null)
                {
                    await storageLease.DisposeAsync();
                }

                try
                {
                    await report.DisposeAsync();
                }
                finally
                {
                    if (snapshotLease is not null)
                    {
                        await snapshotLease.DisposeAsync();
                    }
                }
            }
        }

        private IBackupSource? CreateSourceProvider(Schedule schedule)
        {
            if (providers.FirstOrDefault(x => x.Id == schedule.Backup.Source.BackupModuleId) is not IBackupSource foundSourceTypeProvider)
            {
                schedule.ErrorMessage = $"Source provider not found: {schedule.Backup.Source.BackupModuleId}";
                schedule.Status = ScheduleStatus.Failed;
                schedule.FinishedAt = DateTime.UtcNow;
                dbContext.SaveChanges();
                logger.LogWarning("{msg}", schedule.ErrorMessage);

                return null;
            }

            IBackupSource foundSourceProvider = (IBackupSource)ActivatorUtilities.CreateInstance(serviceProvider, foundSourceTypeProvider.GetType());
            foundSourceProvider.SetParameters(schedule.Backup.Source.Params(crypto).Snapshot());
            return foundSourceProvider;
        }

        private IBackupStorage? CreateStorageProvider(Schedule schedule)
        {
            if (providers.FirstOrDefault(x => x.Id == schedule.Backup.Storage.BackupModuleId) is not IBackupStorage foundStorageTypeProvider)
            {
                schedule.ErrorMessage = $"Storage provider not found: {schedule.Backup.Storage.BackupModuleId}";
                schedule.Status = ScheduleStatus.Failed;
                schedule.FinishedAt = DateTime.UtcNow;
                dbContext.SaveChanges();
                logger.LogWarning("{msg}", schedule.ErrorMessage);

                return null;
            }

            IBackupStorage foundStorageProvider = (IBackupStorage)ActivatorUtilities.CreateInstance(serviceProvider, foundStorageTypeProvider.GetType());
            foundStorageProvider.SetParameters(schedule.Backup.Storage.Params(crypto).Snapshot());
            return foundStorageProvider;
        }

        private async Task BackupAsync(
            Schedule schedule,
            IBackupSource source,
            IBackupStorage storage,
            ScheduleReport report,
            IEnumerable<BackupFileInfo> lazyFiles,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<Guid> incrementalSnapshotIds = await GetIncrementalSnapshotIdsAsync(
                schedule.BackupId,
                cancellationToken);
            SnapshotBatchWriter snapshotWriter = new(dbContext);
            Snapshot snapshot = await snapshotWriter.CreateAsync(
                schedule.BackupId,
                schedule,
                cancellationToken);
            using LazyLoader<BackupFileInfo> loader = new(
                lazyFiles,
                PreviousFilesBatchSize * PrefetchedFileBatchCount);
            HashSet<ChunkKeyIdentity> uploadedChunks = await LoadChunkHashesAsync(
                schedule.Backup.StorageId,
                cancellationToken);
            logger.LogInformation(
                "Using {SnapshotCount} snapshot layers for incremental lookup",
                incrementalSnapshotIds.Count);

            int counter = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            cancellationToken.ThrowIfCancellationRequested();
            foreach (BackupFileInfo[] filesBatch in loader.GetBatches(
                PreviousFilesBatchSize,
                PreviousFilesBatchIdleTimeout,
                cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<string, SnapshotFile> previousFiles = await GetPreviousFilesAsync(
                    incrementalSnapshotIds,
                    filesBatch,
                    cancellationToken);

                foreach (BackupFileInfo file in filesBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counter = await ProcessFileAsync(
                        schedule,
                        snapshot,
                        snapshotWriter,
                        source,
                        storage,
                        report,
                        loader,
                        uploadedChunks,
                        previousFiles,
                        stopwatch,
                        counter,
                        file,
                        cancellationToken);
                }
            }

            await FinalizeSnapshotAsync(
                schedule,
                snapshot,
                snapshotWriter,
                loader,
                report,
                cancellationToken);
        }

        private async Task<int> ProcessFileAsync(
            Schedule schedule,
            Snapshot snapshot,
            SnapshotBatchWriter snapshotWriter,
            IBackupSource source,
            IBackupStorage storage,
            ScheduleReport report,
            LazyLoader<BackupFileInfo> loader,
            HashSet<ChunkKeyIdentity> uploadedChunks,
            IDictionary<string, SnapshotFile> previousFiles,
            Stopwatch stopwatch,
            int counter,
            BackupFileInfo file,
            CancellationToken cancellationToken)
        {
            counter++;
            report.Total = loader.Total;
            report.IsEnumerationCompleted = loader.IsEnumerationCompleted;
            report.CurrentFile = file.Name ?? Path.GetFileName(file.Path);
            report.CurrentPath = file.Path;
            await report.SendAsync(counter, $"Processing: {file.Name}", cancellationToken: cancellationToken);

            if (ShouldIgnoreFile(schedule, file))
            {
                logger.LogInformation("Schedule {ScheduleId}: File {FileName} is ignored by path rules, skipping",
                    schedule.Id, file.Name);
                return counter;
            }

            cancellationToken.ThrowIfCancellationRequested();
            previousFiles.TryGetValue(file.Path, out SnapshotFile? previousFile);

            if (previousFile != null && CanReusePreviousFile(previousFile, file, out bool datesMatch))
            {
                await ReusePreviousFileAsync(
                    schedule,
                    snapshot,
                    snapshotWriter,
                    previousFile,
                    file,
                    datesMatch,
                    stopwatch,
                    report,
                    counter,
                    cancellationToken);
                return counter;
            }

            // Diagnostic: why file was not skipped
            if (previousFile != null)
            {
                bool diagnosticDatesMatch = previousFile.LastModified == null || file.LastModified == null ||
                    Math.Abs((previousFile.LastModified.Value - file.LastModified.Value).TotalSeconds) < 2;

                logger.LogWarning("File {FileName} NOT skipped - foundFile!=null: true, hasHashsum: {HasHashsum}, sizeMatch: " +
                    "{SizeMatch} ({OldSize} vs {NewSize}), datesMatch: {DatesMatch}, oldDate: {OldDate}, newDate: {NewDate}, diffSec: {DiffSec}",
                    file.Name,
                    previousFile.Hashsum != null,
                    file.Size == previousFile.Size, previousFile.Size, file.Size,
                    diagnosticDatesMatch,
                    previousFile.LastModified?.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    file.LastModified?.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    previousFile.LastModified != null && file.LastModified != null
                        ? Math.Abs((previousFile.LastModified.Value - file.LastModified.Value).TotalSeconds).ToString("F3")
                        : "N/A");
            }
            else
            {
                logger.LogInformation("File {FileName} NOT found in previous snapshot", file.Path);
            }

            if (previousFile != null)
            {
                logger.LogInformation("File {FileName} changed - HasHashsum: {HasHashsum}, Size: {OldSize} vs {NewSize}, LastModified: {OldModified} vs {NewModified} (diff: {DiffSeconds}s)",
                    file.Name, previousFile.Hashsum != null, previousFile.Size, file.Size,
                    previousFile.LastModified?.ToString("yyyy-MM-dd HH:mm:ss"), file.LastModified?.ToString("yyyy-MM-dd HH:mm:ss"),
                    previousFile.LastModified != null && file.LastModified != null
                        ? Math.Abs((previousFile.LastModified.Value - file.LastModified.Value).TotalSeconds)
                        : -1);
            }

            using Stream stream = await source.GetFileStreamAsync(file, cancellationToken);
            if (stream == Stream.Null)
            {
                logger.LogWarning("Unable to get stream for file {FileName}, skipping", file.Name);
                return counter;
            }

            (string? fileHash, List<string>? chunkHashes) = await ProcessChunksAsync(
                schedule,
                file,
                storage,
                report,
                uploadedChunks,
                stream,
                counter,
                cancellationToken);

            SnapshotFile snapshotFile = new()
            {
                Path = file.Path,
                Hashsum = fileHash,
                Size = file.Size ?? 0,
                ChunkHashes = chunkHashes,
                Name = file.Name ?? file.Path,
                LastModified = file.LastModified,
            };
            await snapshotWriter.AddFileAsync(snapshot, snapshotFile, cancellationToken);
            await PersistSnapshotIfDueAsync(
                schedule,
                snapshot,
                snapshotWriter,
                stopwatch,
                cancellationToken);

            logger.LogInformation("Schedule {ScheduleId}: {Message} ({Processed}/{Total})",
                schedule.Id, report.Message, report.Processed, report.Total);

            return counter;
        }

    }
}
