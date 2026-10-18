// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions;
using EasyExtensions.Abstractions;
using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Quartz.Extensions;
using Mapster;
using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Jobs;
using Octockup.Server.Handlers.Deletion;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Enums;
using Octockup.Server.Models.Requests;
using Quartz;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text.Json;

namespace Octockup.Server.Controllers
{
    [ApiController]
    public class BackupController(
        AppDbContext _dbContext,
        IStreamCipher _streamCipher,
        ISchedulerFactory _schedulerFactory,
        ILogger<BackupController> _logger) : ControllerBase
    {
        [Authorize]
        [HttpGet("/api/v1/backups/server")]
        public async Task<IActionResult> GetServerBackup([FromQuery] bool includeFiles = false, CancellationToken ct = default)
        {
            Guid userId = User.GetUserId();

            User? user = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user == null)
            {
                return this.ApiNotFound("User not found: " + userId);
            }

            _logger.LogInformation("User {UserId} requested server backup data.", userId);

            List<Module> modules = await _dbContext.Modules
                .AsNoTracking()
                .Where(m => m.UserId == userId)
                .ToListAsync(ct);
            foreach (Module? item in modules)
            {
                IReadOnlyDictionary<string, string> paramsDict = item.Params(_streamCipher).Snapshot();
                foreach (KeyValuePair<string, string> param in paramsDict)
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    item.Parameters[param.Key] = param.Value;
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }

            _logger.LogInformation("Exported {ModuleCount} modules for user {UserId}.", modules.Count, userId);

            List<Guid> moduleIds = modules.Select(m => m.Id).ToList();

            List<Backup> backups = await _dbContext.Backups
                .AsNoTracking()
                .Where(b => moduleIds.Contains(b.SourceId))
                .ToListAsync(ct);

            _logger.LogInformation("Exported {BackupCount} backups for user {UserId}.", backups.Count, userId);

            List<Guid> backupIds = backups.Select(b => b.Id).ToList();

            List<Schedule> schedules = await _dbContext.Schedules
                .AsNoTracking()
                .Where(s => backupIds.Contains(s.BackupId))
                .ToListAsync(ct);

            _logger.LogInformation("Exported {ScheduleCount} schedules for user {UserId}.", schedules.Count, userId);

            List<Snapshot> snapshots = await _dbContext.Snapshots
                .AsNoTracking()
                .Where(s => backupIds.Contains(s.BackupId))
                .ToListAsync(ct);

            _logger.LogInformation("Exported {SnapshotCount} snapshots for user {UserId}.", snapshots.Count, userId);

            List<Guid> snapshotIds = includeFiles ? snapshots.Select(s => s.Id).ToList() : [];
            List<SnapshotFile> snapshotFiles = includeFiles ? await _dbContext.SnapshotFiles
                .AsNoTracking()
                .Where(sf => snapshotIds.Contains(sf.SnapshotId))
                .ToListAsync(ct) : [];

            _logger.LogInformation("Exported {SnapshotFileCount} snapshot files for user {UserId}.", snapshotFiles.Count, userId);

            Response.ContentType = "application/octet-stream";
            Response.Headers.ContentDisposition =
                $"attachment; filename=\"server-backup-{userId}.{CompressionHelpers.Extension}\"";

            await using Stream compressedStream = CompressionHelpers.CreateCompressionStream(Response.Body);

            // Stream JSON through a Pipe to the encryptor to avoid buffering everything in memory.
            Pipe pipe = new Pipe();
            PipeWriter writer = pipe.Writer;
            PipeReader reader = pipe.Reader;

            Task encryptTask = Task.Run(async () =>
            {
                await using Stream inputStream = reader.AsStream(leaveOpen: false);
                await _streamCipher.EncryptAsync(inputStream, compressedStream, ct: ct);
            }, ct);

            Task serializeTask = Task.Run(async () =>
            {
                await using Stream outputStream = writer.AsStream(leaveOpen: false);
                await JsonSerializer.SerializeAsync(
                    outputStream,
                    new
                    {
                        Modules = modules,
                        Backups = backups,
                        Schedules = schedules,
                        Snapshots = snapshots,
                        SnapshotFiles = snapshotFiles
                    }, cancellationToken: ct);
                await writer.CompleteAsync();
            }, ct);

            await Task.WhenAll(encryptTask, serializeTask);

            await compressedStream.FlushAsync(ct);
            return new EmptyResult();
        }

        [Authorize]
        [HttpPatch("/api/v1/backups/{backupId:guid}/ignored-paths")]
        public async Task<IActionResult> UpdateIgnoredPaths([FromRoute] Guid backupId, [FromBody] List<string> ignoredPaths)
        {
            Guid userId = User.GetUserId();
            Backup? backup = await _dbContext.Backups
                .FirstOrDefaultAsync(x =>
                    x.Id == backupId &&
                    x.Source.UserId == userId &&
                    x.Storage.UserId == userId);
            if (backup == null)
            {
                return this.ApiNotFound("Backup not found: " + backupId);
            }
            Module? source = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == backup.SourceId && m.UserId == userId && m.Destination == ModuleDestination.Source);
            if (source == null)
            {
                return this.ApiNotFound("Source module not found for backup: " + backupId);
            }
            Module? storage = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == backup.StorageId && m.UserId == userId && m.Destination == ModuleDestination.Target);
            if (storage == null)
            {
                return this.ApiNotFound("Storage module not found for backup: " + backupId);
            }
            backup.IgnoredPaths = ignoredPaths;
            await _dbContext.SaveChangesAsync();
            return Ok(new { message = "Ignored paths updated successfully." });
        }

        [Authorize]
        [HttpPatch("/api/v1/backups/{backupId:guid}/rename")]
        public async Task<IActionResult> RenameBackup([FromRoute] Guid backupId, [FromBody] RenameModuleRequest request)
        {
            Guid userId = User.GetUserId();
            Backup? backup = await _dbContext.Backups
                .FirstOrDefaultAsync(x =>
                    x.Id == backupId &&
                    x.Source.UserId == userId &&
                    x.Storage.UserId == userId);
            if (backup == null)
            {
                return this.ApiNotFound("Backup not found: " + backupId);
            }
            Module? source = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == backup.SourceId && m.UserId == userId && m.Destination == ModuleDestination.Source);
            if (source == null)
            {
                return this.ApiNotFound("Source module not found for backup: " + backupId);
            }
            Module? storage = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == backup.StorageId && m.UserId == userId && m.Destination == ModuleDestination.Target);
            if (storage == null)
            {
                return this.ApiNotFound("Storage module not found for backup: " + backupId);
            }
            bool existsTag = await _dbContext.Backups.AnyAsync(b => b.Tag == request.NewTag && b.Id != backupId);
            if (existsTag)
            {
                return this.ApiBadRequest("Tag already exists: " + request.NewTag);
            }
            backup.Tag = request.NewTag;
            await _dbContext.SaveChangesAsync();
            return Ok(new { message = "Backup renamed successfully." });
        }

        [Authorize]
        [HttpGet("/api/v1/backups")]
        public async Task<IEnumerable<BackupDto>> GetUserBackups()
        {
            Guid userId = User.GetUserId();
            return await _dbContext.Backups
                .AsNoTracking()
                .AsSplitQuery()
                .Include(x => x.Source)
                .Include(x => x.Storage)
                .Include(x => x.Snapshots)
                .Include(x => x.Schedules)
                .Where(x => x.Source.UserId == userId)
                .ProjectToType<BackupDto>()
                .ToListAsync();
        }

        [Authorize]
        [HttpPost("/api/v1/backups")]
        public async Task<IActionResult> CreateBackup([FromBody] CreateBackupRequest request)
        {
            Guid userId = User.GetUserId();
            Module? source = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == request.SourceId && m.UserId == userId && m.Destination == ModuleDestination.Source);
            if (source == null)
            {
                return this.ApiNotFound("Source module not found: " + request.SourceId);
            }
            Module? storage = await _dbContext.Modules.FirstOrDefaultAsync(m => m.Id == request.StorageId && m.UserId == userId && m.Destination == ModuleDestination.Target);
            if (storage == null)
            {
                return this.ApiNotFound("Storage module not found: " + request.StorageId);
            }
            bool existsTag = await _dbContext.Backups.AnyAsync(b => b.Tag == request.Tag);
            if (existsTag)
            {
                return this.ApiBadRequest("Tag already exists: " + request.Tag);
            }
            Backup backup = new()
            {
                Tag = request.Tag,
                SourceId = source.Id,
                StorageId = storage.Id,
                IgnoredPaths = request.IgnoredPaths ?? [],
                DisableCompression = request.DisableCompression,
                DisableEncryption = request.DisableEncryption
            };
            await _dbContext.Backups.AddAsync(backup);
            await _dbContext.SaveChangesAsync();
            return Ok(new { message = "Backup created successfully." });
        }

        [Authorize]
        [HttpDelete("/api/v1/backups/{backupId:guid}")]
        public async Task<IActionResult> DeleteBackup(
            [FromRoute] Guid backupId,
            [FromServices] IMediator mediator,
            CancellationToken cancellationToken = default)
        {
            bool found = await mediator.Send(
                new RequestBackupDeletionCommand(User.GetUserId(), backupId), cancellationToken);
            return found ? Accepted() : NotFound();
        }

        [Authorize]
        [RequestSizeLimit(1_000_000_000)]
        [HttpPost("/api/v1/backups/server/import")]
        public async Task<IActionResult> ImportServerBackup([FromForm] IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
            {
                return this.ApiBadRequest("File is required");
            }

            Guid userId = User.GetUserId();
            User? user = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user == null)
            {
                return this.ApiNotFound("User not found: " + userId);
            }

            _logger.LogInformation("User {UserId} is importing server backup data, file size: {FileSize} bytes", userId, file.Length);

            // Create import directory if not exists
            string importDir = Path.Combine(Path.GetTempPath(), "octockup-imports", userId.ToString());
            Directory.CreateDirectory(importDir);

            // Save uploaded file
            string fileName = $"import-{DateTime.UtcNow:yyyyMMddHHmmss}.{CompressionHelpers.Extension}";
            string filePath = Path.Combine(importDir, fileName);

            _logger.LogInformation("Saving import file for user {UserId} to {FilePath}", userId, filePath);
            await using (FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                await file.CopyToAsync(fileStream, ct);
            }

            _logger.LogInformation("Saved import file for user {UserId} to {FilePath}, triggering import job", userId, filePath);

            // Trigger the import job
            await _schedulerFactory.TriggerJobAsync<ImportBackupJob>();

            return Ok(new { message = "Import file uploaded successfully. Processing will begin shortly." });
        }
    }
}
