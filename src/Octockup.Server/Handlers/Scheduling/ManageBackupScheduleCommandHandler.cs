// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;

namespace Octockup.Server.Handlers.Scheduling
{
    public class ManageBackupScheduleCommandHandler(
        AppDbContext dbContext,
        IBackupJobScheduler jobScheduler,
        StorageOperationCoordinator operations)
        : IRequestHandler<ManageBackupScheduleCommand, Guid?>
    {
        public async Task<Guid?> Handle(
            ManageBackupScheduleCommand request,
            CancellationToken cancellationToken)
        {
            Validate(request);

            await using StorageOperationLease lease = await operations.AcquireSnapshotUseAsync(
                request.BackupId, cancellationToken);

            bool backupExists = await dbContext.Backups
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == request.BackupId && x.Source.UserId == request.UserId,
                    cancellationToken);
            if (!backupExists)
            {
                throw new AuthApiException(
                    StatusCodes.Status404NotFound,
                    $"Backup not found: {request.BackupId}");
            }

            if (await dbContext.Backups.AnyAsync(x => x.Id == request.BackupId &&
                (x.DeletionRequestedAt != null || x.Snapshots.Any(s => s.DeletionRequestedAt != null)), cancellationToken))
            {
                throw new AuthApiException(StatusCodes.Status409Conflict, "Backup metadata deletion is pending.");
            }

            List<Schedule> schedules = await dbContext.Schedules
                .Where(x => x.BackupId == request.BackupId)
                .OrderByDescending(x => x.UpdatedAt)
                .ToListAsync(cancellationToken);

            return request.Action switch
            {
                BackupScheduleAction.RunNow => await RunNowAsync(
                    request.BackupId,
                    schedules,
                    cancellationToken),
                BackupScheduleAction.SetInterval => await SetIntervalAsync(
                    request.BackupId,
                    schedules,
                    request.IntervalMinutes!.Value,
                    cancellationToken),
                BackupScheduleAction.Disable => await DisableAsync(
                    schedules,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request.Action),
                    request.Action,
                    null),
            };
        }

        private static void Validate(ManageBackupScheduleCommand request)
        {
            if (!Enum.IsDefined(request.Action))
            {
                throw new AuthApiException(
                    StatusCodes.Status400BadRequest,
                    $"Invalid backup schedule action: {request.Action}");
            }

            if (request.Action == BackupScheduleAction.SetInterval &&
                request.IntervalMinutes is not > 0)
            {
                throw new AuthApiException(
                    StatusCodes.Status400BadRequest,
                    "Schedule interval must be greater than zero minutes.");
            }
        }

        private async Task<Guid?> RunNowAsync(
            Guid backupId,
            IReadOnlyList<Schedule> schedules,
            CancellationToken cancellationToken)
        {
            Schedule? schedule = schedules.FirstOrDefault(x => x.Status == ScheduleStatus.Running)
                ?? schedules.FirstOrDefault(x => x.Interval is not null)
                ?? GetFirstSchedule(schedules);

            if (schedule is null)
            {
                schedule = new Schedule
                {
                    BackupId = backupId,
                };
                await dbContext.Schedules.AddAsync(schedule, cancellationToken);
            }

            if (schedule.Status != ScheduleStatus.Running)
            {
                schedule.StartAt = DateTime.UtcNow;
                schedule.Status = ScheduleStatus.Created;
                schedule.FinishedAt = null;
                schedule.ErrorMessage = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await jobScheduler.TriggerAsync();
            return schedule.Id;
        }

        private async Task<Guid?> SetIntervalAsync(
            Guid backupId,
            IReadOnlyList<Schedule> schedules,
            int intervalMinutes,
            CancellationToken cancellationToken)
        {
            List<Schedule> recurringSchedules = schedules
                .Where(x => x.Interval is not null)
                .ToList();
            List<Schedule> runningRecurringSchedules = recurringSchedules
                .Where(x => x.Status == ScheduleStatus.Running)
                .ToList();
            if (runningRecurringSchedules.Count > 1)
            {
                throw new AuthApiException(
                    StatusCodes.Status409Conflict,
                    "Multiple recurring schedules are currently running for this backup.");
            }

            Schedule? schedule = GetOnlySchedule(runningRecurringSchedules)
                ?? GetFirstSchedule(recurringSchedules)
                ?? schedules.FirstOrDefault(x => x.Status == ScheduleStatus.Running)
                ?? GetFirstSchedule(schedules);
            TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);

            if (schedule is null)
            {
                schedule = new Schedule
                {
                    BackupId = backupId,
                    StartAt = DateTime.UtcNow.Add(interval),
                    Status = ScheduleStatus.Created,
                    Interval = interval,
                };
                await dbContext.Schedules.AddAsync(schedule, cancellationToken);
            }
            else if (schedule.Status == ScheduleStatus.Running)
            {
                schedule.Interval = interval;
            }
            else
            {
                schedule.StartAt = DateTime.UtcNow.Add(interval);
                schedule.Status = ScheduleStatus.Created;
                schedule.Interval = interval;
                schedule.FinishedAt = null;
                schedule.ErrorMessage = null;
            }

            Schedule[] duplicates = recurringSchedules
                .Where(x => x.Id != schedule.Id)
                .ToArray();
            dbContext.Schedules.RemoveRange(duplicates);
            await dbContext.SaveChangesAsync(cancellationToken);
            await jobScheduler.TriggerAsync();
            return schedule.Id;
        }

        private async Task<Guid?> DisableAsync(
            IReadOnlyList<Schedule> schedules,
            CancellationToken cancellationToken)
        {
            List<Schedule> recurringSchedules = schedules
                .Where(x => x.Interval is not null)
                .ToList();
            List<Schedule> runningSchedules = recurringSchedules
                .Where(x => x.Status == ScheduleStatus.Running)
                .ToList();
            if (runningSchedules.Count > 1)
            {
                throw new AuthApiException(
                    StatusCodes.Status409Conflict,
                    "Multiple recurring schedules are currently running for this backup.");
            }

            Schedule? runningSchedule = GetOnlySchedule(runningSchedules);
            if (runningSchedule is not null)
            {
                runningSchedule.Interval = null;
            }

            Schedule[] removableSchedules = recurringSchedules
                .Where(x => x.Id != runningSchedule?.Id)
                .ToArray();
            dbContext.Schedules.RemoveRange(removableSchedules);
            await dbContext.SaveChangesAsync(cancellationToken);
            return runningSchedule?.Id;
        }

        private static Schedule? GetFirstSchedule(IReadOnlyList<Schedule> schedules)
        {
            return schedules.Count > 0 ? schedules[0] : null;
        }

        private static Schedule? GetOnlySchedule(IReadOnlyList<Schedule> schedules)
        {
            return schedules.Count == 1 ? schedules[0] : null;
        }
    }
}
