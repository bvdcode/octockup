// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions;
using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Quartz.Extensions;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Jobs;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Enums;
using Octockup.Server.Models.Requests;
using Quartz;
using Octockup.Server.Services;

namespace Octockup.Server.Controllers
{
    [ApiController]
    public class ScheduleController(
        AppDbContext _dbContext,
        ISchedulerFactory _scheduler) : ControllerBase
    {
        [Authorize]
        [HttpGet("/api/v1/schedules")]
        public async Task<IEnumerable<ScheduleDto>> GetUserSchedules()
        {
            Guid userId = User.GetUserId();
            return await _dbContext.Schedules
                .AsNoTracking()
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Source)
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Storage)
                .Where(s => s.Backup.Source.UserId == userId)
                .ProjectToType<ScheduleDto>()
                .ToListAsync();
        }

        [Authorize]
        [HttpPost("/api/v1/schedules/{scheduleId:guid}/reset-error")]
        public async Task<IActionResult> Reschedule(Guid scheduleId)
        {
            Guid userId = User.GetUserId();
            Schedule? schedule = await _dbContext.Schedules
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Source)
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.Backup.Source.UserId == userId);
            if (schedule == null)
            {
                return this.ApiNotFound("Schedule not found: " + scheduleId);
            }
            if (schedule.Status != ScheduleStatus.Failed)
            {
                return this.ApiBadRequest("Only schedules in Error status can be reset.");
            }
            schedule.Status = ScheduleStatus.Created;
            schedule.ErrorMessage = null;
            schedule.FinishedAt = null;
            await _dbContext.SaveChangesAsync();
            await _scheduler.TriggerJobAsync<ExecuteBackupJob>();
            return Ok(new { message = "Schedule error reset successfully." });
        }

        [Authorize]
        [HttpPost("/api/v1/schedules/{scheduleId:guid}/cancel")]
        public async Task<IActionResult> CancelSchedule(Guid scheduleId)
        {
            Guid userId = User.GetUserId();
            bool isOwned = await _dbContext.Schedules
                .AsNoTracking()
                .AnyAsync(s => s.Id == scheduleId && s.Backup.Source.UserId == userId);
            if (!isOwned)
            {
                return this.ApiNotFound("Schedule not found: " + scheduleId);
            }

            ExecuteBackupJob.StopRunningBackup(scheduleId);
            await _scheduler.TriggerJobAsync<ExecuteBackupJob>();
            return Ok(new { message = "Schedule cancellation requested." });
        }

        [Authorize]
        [HttpPost("/api/v1/schedules")]
        public async Task<IActionResult> CreateSchedule(
            [FromBody] CreateScheduleRequest request,
            [FromServices] StorageOperationCoordinator operations)
        {
            Guid userId = User.GetUserId();
            await using StorageOperationLease lease = await operations.AcquireSnapshotUseAsync(
                request.BackupId, HttpContext.RequestAborted);
            Backup? backup = await _dbContext.Backups
                .Include(b => b.Source)
                .FirstOrDefaultAsync(b => b.Id == request.BackupId && b.Source.UserId == userId);
            if (backup == null)
            {
                return this.ApiNotFound("Backup not found: " + request.BackupId);
            }
            if (backup.DeletionRequestedAt is not null || await _dbContext.Snapshots.AnyAsync(
                x => x.BackupId == backup.Id && x.DeletionRequestedAt != null))
            {
                return Conflict("Backup metadata deletion is pending.");
            }
            if (request.StartAt.Kind != DateTimeKind.Utc)
            {
                return this.ApiBadRequest("StartAt must be in UTC.");
            }

            Schedule schedule = new()
            {
                BackupId = backup.Id,
                StartAt = request.StartAt,
                Status = ScheduleStatus.Created,
                Interval = request.IntervalMinutes.HasValue ? TimeSpan.FromMinutes(request.IntervalMinutes.Value) : null,
            };

            await _dbContext.Schedules.AddAsync(schedule);
            await _dbContext.SaveChangesAsync();
            await _scheduler.TriggerJobAsync<ExecuteBackupJob>();
            return Ok(new { message = "Schedule created successfully." });
        }

        [Authorize]
        [HttpDelete("/api/v1/schedules/{scheduleId}")]
        public async Task<IActionResult> DeleteSchedule(Guid scheduleId)
        {
            Guid userId = User.GetUserId();
            Schedule? schedule = await _dbContext.Schedules
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Source)
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.Backup.Source.UserId == userId);

            if (schedule == null)
            {
                return this.ApiNotFound("Schedule not found: " + scheduleId);
            }

            _dbContext.Schedules.Remove(schedule);
            await _dbContext.SaveChangesAsync();
            return Ok(new { message = "Schedule deleted successfully." });
        }
    }
}
