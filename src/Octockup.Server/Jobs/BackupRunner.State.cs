// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using Microsoft.EntityFrameworkCore;
using Octockup.Server.Collections;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models;
using Octockup.Server.Models.Enums;
using System.Diagnostics;

namespace Octockup.Server.Jobs
{
    public partial class BackupRunner
    {
        private static bool CanReusePreviousFile(
            SnapshotFile previousFile,
            BackupFileInfo currentFile,
            out bool datesMatch)
        {
            datesMatch = previousFile.LastModified == null || currentFile.LastModified == null ||
                Math.Abs((previousFile.LastModified.Value - currentFile.LastModified.Value).TotalSeconds) < 2;
            return previousFile.Hashsum != null && currentFile.Size == previousFile.Size && datesMatch;
        }

        private async Task HandleCancellationAsync(Schedule schedule, ScheduleReport report)
        {
            logger.LogInformation("Schedule {ScheduleId} backup canceled", schedule.Id);
            schedule.Status = ScheduleStatus.Failed;
            schedule.ErrorMessage = "Backup was canceled.";
            schedule.FinishedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await report.SendAsync(
                report.Processed,
                "Backup canceled.",
                status: ScheduleStatus.Failed,
                cancellationToken: CancellationToken.None);
        }

        private async Task ReusePreviousFileAsync(
            Schedule schedule,
            Snapshot snapshot,
            SnapshotBatchWriter snapshotWriter,
            SnapshotFile previousFile,
            BackupFileInfo currentFile,
            bool datesMatch,
            Stopwatch stopwatch,
            ScheduleReport report,
            int counter,
            CancellationToken cancellationToken)
        {
            logger.LogInformation(
                "File {FileName} unchanged since last snapshot (size: {Size}, date match: {DateMatch}), reusing metadata",
                currentFile.Name,
                currentFile.Size,
                datesMatch);
            SnapshotFile snapshotFile = new()
            {
                Path = currentFile.Path,
                Size = currentFile.Size ?? 0,
                Hashsum = previousFile.Hashsum,
                Name = currentFile.Name ?? currentFile.Path,
                LastModified = currentFile.LastModified,
                ChunkHashes = previousFile.ChunkHashes,
            };
            await snapshotWriter.AddFileAsync(snapshot, snapshotFile, cancellationToken);
            await PersistSnapshotIfDueAsync(
                schedule,
                snapshot,
                snapshotWriter,
                stopwatch,
                cancellationToken);
            await report.SendAsync(
                counter,
                $"Processing: {currentFile.Name}",
                processedBytes: snapshotFile.Size,
                cancellationToken: cancellationToken);
        }

        private static async Task PersistSnapshotIfDueAsync(
            Schedule schedule,
            Snapshot snapshot,
            SnapshotBatchWriter snapshotWriter,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            if (stopwatch.Elapsed < SnapshotFlushInterval)
            {
                return;
            }

            await snapshotWriter.FlushAsync(snapshot, schedule, cancellationToken);
            stopwatch.Restart();
        }

        private async Task FinalizeSnapshotAsync(
            Schedule schedule,
            Snapshot snapshot,
            SnapshotBatchWriter snapshotWriter,
            LazyLoader<BackupFileInfo> loader,
            ScheduleReport report,
            CancellationToken cancellationToken)
        {
            await FlushUploadedHashesAsync(cancellationToken);
            report.Total = loader.Total;
            report.IsEnumerationCompleted = true;
            await report.SendAsync(
                report.Processed,
                "Finalizing snapshot...",
                cancellationToken: cancellationToken);
            await snapshotWriter.CompleteAsync(snapshot, schedule, cancellationToken);
        }

        private Task<List<Guid>> GetIncrementalSnapshotIdsAsync(
            Guid backupId,
            CancellationToken cancellationToken)
        {
            return dbContext.Snapshots
                .AsNoTracking()
                .Where(x => x.BackupId == backupId)
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        private async Task<Dictionary<string, SnapshotFile>> GetPreviousFilesAsync(
            IReadOnlyList<Guid> snapshotIds,
            IReadOnlyCollection<BackupFileInfo> files,
            CancellationToken cancellationToken)
        {
            if (snapshotIds.Count == 0 || files.Count == 0)
            {
                return [];
            }

            HashSet<string> remainingPaths = files
                .Select(x => x.Path)
                .ToHashSet(StringComparer.Ordinal);
            Dictionary<string, SnapshotFile> previousFiles = new(StringComparer.Ordinal);
            foreach (Guid snapshotId in snapshotIds)
            {
                string[] paths = remainingPaths.ToArray();
                List<SnapshotFile> foundFiles = await dbContext.SnapshotFiles
                    .AsNoTracking()
                    .Where(x => x.SnapshotId == snapshotId && paths.Contains(x.Path))
                    .ToListAsync(cancellationToken);
                foreach (SnapshotFile foundFile in foundFiles)
                {
                    if (remainingPaths.Remove(foundFile.Path))
                    {
                        previousFiles.Add(foundFile.Path, foundFile);
                    }
                }

                if (remainingPaths.Count == 0)
                {
                    break;
                }
            }

            return previousFiles;
        }

        private async Task<HashSet<ChunkKeyIdentity>> LoadChunkHashesAsync(
            Guid storageId,
            CancellationToken cancellationToken)
        {
            IQueryable<string> query = dbContext.UploadedHashes
                .AsNoTracking()
                .Where(x => x.ModuleId == storageId)
                .Select(x => x.Hash);
            query = _keyScope is null
                ? query.Where(x => !x.StartsWith("v3-"))
                : query.Where(x => x.StartsWith("v3-" + _keyScope + "-"));
            int hashCount = await query.CountAsync(cancellationToken);
            HashSet<ChunkKeyIdentity> uploadedChunks = new(hashCount);
            await foreach (string hash in query
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken))
            {
                uploadedChunks.Add(ChunkKeyIdentity.Parse(hash));
            }

            return uploadedChunks;
        }

        private static bool ShouldIgnoreFile(Schedule schedule, BackupFileInfo file)
        {
            if (schedule.Backup.IgnoredPaths is null)
            {
                return false;
            }

            return ScheduleHelpers.IsPathIgnored(
                file.Path,
                file.Name,
                schedule.Backup.IgnoredPaths);
        }
    }
}
