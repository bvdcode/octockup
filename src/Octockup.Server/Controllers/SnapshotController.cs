// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions;
using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Models.Enums;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Octockup.Server.Abstractions;
using Octockup.Server.Archives;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models.Dto;
using Octockup.Server.Streams;

namespace Octockup.Server.Controllers
{
    [ApiController]
    public class SnapshotController(
        IStreamCipher _crypto,
        AppDbContext _dbContext,
        ILogger<SnapshotController> _logger,
        IEnumerable<IBackupProvider> _providers) : ControllerBase
    {
        [Authorize]
        [HttpGet("/api/v1/snapshots/{snapshotId:guid}/download")]
        public async Task<IActionResult> DownloadSnapshotArchive(
            [FromRoute] Guid snapshotId,
            CancellationToken cancellationToken,
            [FromQuery] bool validate = false)
        {
            Guid userId = User.GetUserId();

            Snapshot? snapshot = await _dbContext.Snapshots
                .AsNoTracking()
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Source)
                .Include(s => s.Backup)
                    .ThenInclude(b => b.Storage)
                .FirstOrDefaultAsync(
                    s => s.Id == snapshotId && s.Backup.Source.UserId == userId,
                    cancellationToken);

            if (snapshot == null)
            {
                return NotFound();
            }

            if (snapshot.CompletedAt == null)
            {
                return BadRequest("Snapshot is not completed.");
            }

            IBackupProvider? provider = _providers
                .FirstOrDefault(p => p.Id == snapshot.Backup.Storage.BackupModuleId);

            if (provider == null)
            {
                return NotFound();
            }

            provider.SetParameters(snapshot.Backup.Storage.Params(_crypto).Snapshot());
            if (provider is not IBackupStorage storage)
            {
                return BadRequest("Storage provider is not a backup storage");
            }

            List<SnapshotFile> snapshotFiles = await _dbContext.SnapshotFiles
                .AsNoTracking()
                .Where(sf => sf.SnapshotId == snapshotId)
                .OrderBy(sf => sf.Path)
                .ThenBy(sf => sf.Name)
                .ToListAsync(cancellationToken);

            Dictionary<Guid, IReadOnlyList<ChunkStorageDescriptor>> chunksByFile;
            try
            {
                chunksByFile = await ResolveChunksByFileAsync(
                    snapshot.Backup.StorageId,
                    snapshotFiles,
                    storage,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                _logger.LogError(ex, "Unsupported chunk metadata while creating archive for snapshot {SnapshotId}.", snapshotId);
                return BadRequest("Unsupported chunk metadata.");
            }

            List<StoredZipArchiveEntry> entries = snapshotFiles
                .Select(snapshotFile => CreateArchiveEntry(
                    snapshotFile,
                    chunksByFile[snapshotFile.Id],
                    storage,
                    cancellationToken,
                    validate))
                .ToList();

            string fileName = SnapshotArchiveFileName.Create(
                snapshot.Backup.Tag,
                snapshot.CreatedAt,
                snapshot.CompletedAt,
                snapshot.Id);

            Response.ContentType = "application/zip";
            Response.ContentLength = StoredZipArchiveWriter.CalculateContentLength(entries);
            Response.Headers.ContentDisposition = SnapshotArchiveFileName.CreateContentDisposition(fileName);

            await StoredZipArchiveWriter
                .WriteAsync(Response.Body, entries, cancellationToken)
                .ConfigureAwait(false);

            return new EmptyResult();
        }

        [Authorize]
        [HttpGet("/api/v1/snapshots/{snapshotId:guid}/files/{fileId:guid}/download")]
        public async Task<IActionResult> DownloadSnapshotFile(
            [FromRoute] Guid snapshotId,
            [FromRoute] Guid fileId,
            [FromQuery] bool validate = false)
        {
            Guid userId = User.GetUserId();
            SnapshotFile? snapshotFile = await _dbContext.SnapshotFiles
                .AsNoTracking()
                .Include(sf => sf.Snapshot)
                    .ThenInclude(s => s.Backup)
                        .ThenInclude(b => b.Storage)
                .Include(sf => sf.Snapshot)
                    .ThenInclude(s => s.Backup)
                        .ThenInclude(b => b.Source)
                .FirstOrDefaultAsync(sf =>
                    sf.SnapshotId == snapshotId &&
                    sf.Id == fileId &&
                    sf.Snapshot.Backup.Source.UserId == userId);

            if (snapshotFile == null)
            {
                return NotFound();
            }

            IBackupProvider? provider = _providers
                .FirstOrDefault(p => p.Id == snapshotFile.Snapshot.Backup.Storage.BackupModuleId);

            if (provider == null)
            {
                return NotFound();
            }

            provider.SetParameters(snapshotFile.Snapshot.Backup.Storage.Params(_crypto).Snapshot());
            if (provider is not IBackupStorage storage)
            {
                return BadRequest("Storage provider is not a backup storage");
            }

            List<string> chunkKeys = snapshotFile.ChunkHashes?.ToList() ?? [];
            Dictionary<string, UploadedHash> uploadedHashes = await _dbContext.UploadedHashes
                .AsNoTracking()
                .Where(x => x.ModuleId == snapshotFile.Snapshot.Backup.StorageId && chunkKeys.Contains(x.Hash))
                .ToDictionaryAsync(x => x.Hash);

            List<ChunkStorageDescriptor> chunks = [];
            MissingChunkSizeResolver sizeResolver = new(_logger, storage, _crypto);
            foreach (string chunkKey in chunkKeys)
            {
                if (uploadedHashes.TryGetValue(chunkKey, out UploadedHash? found))
                {
                    chunks.Add(ChunkStorageHelpers.Parse(found.Hash, found.CompressionAlgorithm, found.OriginalSize));
                    continue;
                }

                _logger.LogWarning("Chunk hash metadata not found in DB: {ChunkKey}", chunkKey);
                try
                {
                    ChunkStorageDescriptor chunk = ChunkStorageHelpers.Parse(chunkKey);
                    chunks.Add(await sizeResolver.ResolveAsync(
                        chunk,
                        snapshotFile,
                        HttpContext.RequestAborted));
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException)
                {
                    _logger.LogError(ex, "Unsupported chunk key metadata: {ChunkKey}", chunkKey);
                    return BadRequest("Unsupported chunk metadata.");
                }
            }

            long restoredSize = GetRestoredFileSize(snapshotFile, chunks);
            SnapshotConcatStream stream = new SnapshotConcatStream(
                _logger,
                storage,
                chunks,
                snapshotFile,
                _crypto,
                HttpContext.RequestAborted,
                restoredSize,
                validate
            );

            string contentType = MimeTypes.GetMimeType(snapshotFile.Name) ?? "application/octet-stream";
            FileStreamResult result = new FileStreamResult(stream, contentType)
            {
                FileDownloadName = snapshotFile.Name,
                EnableRangeProcessing = false,
            };

            Response.ContentLength = restoredSize;
            return result;
        }

        [Authorize]
        [HttpGet("/api/v1/snapshots/{snapshotId:guid}/files")]
        public IActionResult GetSnapshot([FromRoute] Guid snapshotId)
        {
            Guid userId = User.GetUserId();
            bool isOwned = _dbContext.Snapshots
                .AsNoTracking()
                .Any(s => s.Id == snapshotId && s.Backup.Source.UserId == userId);
            if (!isOwned)
            {
                return NotFound();
            }

            List<SnapshotFileDto> snapshotFiles = _dbContext.SnapshotFiles
                .AsNoTracking()
                .Where(sf => sf.SnapshotId == snapshotId)
                .OrderBy(sf => sf.Path)
                .ThenBy(sf => sf.Name)
                .ToList()
                .Adapt<List<SnapshotFileDto>>();
            return Ok(snapshotFiles);
        }

        [Authorize]
        [HttpDelete("/api/v1/snapshots/{snapshotId:guid}")]
        public async Task<IActionResult> DeleteSnapshot([FromRoute] Guid snapshotId)
        {
            Guid userId = User.GetUserId();
            Snapshot? snapshot = await _dbContext.Snapshots
                .FirstOrDefaultAsync(s =>
                    s.Id == snapshotId &&
                    s.Backup.Source.UserId == userId);
            if (snapshot == null)
            {
                return NotFound();
            }
            await _dbContext.SnapshotFiles
                .Where(sf => sf.SnapshotId == snapshotId)
                .ExecuteDeleteAsync();
            _dbContext.Snapshots.Remove(snapshot);
            await _dbContext.SaveChangesAsync();
            return NoContent();
        }

        [Authorize]
        [HttpGet("/api/v1/snapshots")]
        public IActionResult GetSnapshots([FromQuery] Guid backupId)
        {
            Guid userId = User.GetUserId();
            bool isOwned = _dbContext.Backups
                .AsNoTracking()
                .Any(b => b.Id == backupId && b.Source.UserId == userId);
            if (!isOwned)
            {
                return NotFound();
            }

            List<Snapshot> snapshots = _dbContext.Snapshots
                .Where(s => s.BackupId == backupId)
                .OrderBy(s => s.CreatedAt)
                .ToList();

            List<SnapshotDto> result = [];
            foreach (Snapshot snapshot in snapshots)
            {
                SnapshotDto dto = snapshot.Adapt<SnapshotDto>();
                dto.FilesCount = _dbContext.SnapshotFiles
                    .Count(sf => sf.SnapshotId == snapshot.Id);
                dto.TotalSize = _dbContext.SnapshotFiles
                    .Where(sf => sf.SnapshotId == snapshot.Id)
                    .Sum(sf => (long?)sf.Size) ?? 0;
                result.Add(dto);
            }
            return Ok(result);
        }

        private StoredZipArchiveEntry CreateArchiveEntry(
            SnapshotFile snapshotFile,
            IReadOnlyList<ChunkStorageDescriptor> chunks,
            IBackupStorage storage,
            CancellationToken requestCancellationToken,
            bool validate)
        {
            string entryName = StoredZipArchiveWriter.NormalizeEntryName(
                snapshotFile.Path,
                snapshotFile.Name.Length > 0 ? snapshotFile.Name : snapshotFile.Id.ToString("N"));

            return new StoredZipArchiveEntry(
                entryName,
                GetRestoredFileSize(snapshotFile, chunks),
                snapshotFile.LastModified,
                cancellationToken =>
                {
                    SnapshotConcatStream stream = new SnapshotConcatStream(
                        _logger,
                        storage,
                        chunks,
                        snapshotFile,
                        _crypto,
                        requestCancellationToken,
                        GetRestoredFileSize(snapshotFile, chunks),
                        validate);

                    return Task.FromResult<Stream>(stream);
                });
        }

        private static long GetRestoredFileSize(
            SnapshotFile snapshotFile,
            IReadOnlyList<ChunkStorageDescriptor> chunks)
        {
            if (chunks.Count > 0 && chunks.All(x => x.OriginalSize.HasValue))
            {
                return chunks.Sum(x => x.OriginalSize!.Value);
            }

            return snapshotFile.Size;
        }

        private async Task<Dictionary<Guid, IReadOnlyList<ChunkStorageDescriptor>>> ResolveChunksByFileAsync(
            Guid storageId,
            IReadOnlyList<SnapshotFile> snapshotFiles,
            IBackupStorage storage,
            CancellationToken cancellationToken)
        {
            List<string> chunkKeys = snapshotFiles
                .SelectMany(x => x.ChunkHashes ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Dictionary<string, UploadedHash> uploadedHashes = new Dictionary<string, UploadedHash>(StringComparer.Ordinal);
            foreach (string[] batch in chunkKeys.Chunk(500))
            {
                List<UploadedHash> found = await _dbContext.UploadedHashes
                    .AsNoTracking()
                    .Where(x => x.ModuleId == storageId && batch.Contains(x.Hash))
                    .ToListAsync(cancellationToken);

                foreach (UploadedHash? item in found)
                {
                    uploadedHashes[item.Hash] = item;
                }
            }

            Dictionary<Guid, IReadOnlyList<ChunkStorageDescriptor>> result = new Dictionary<Guid, IReadOnlyList<ChunkStorageDescriptor>>();
            MissingChunkSizeResolver sizeResolver = new(_logger, storage, _crypto);
            foreach (SnapshotFile snapshotFile in snapshotFiles)
            {
                List<ChunkStorageDescriptor> chunkDescriptors = new List<ChunkStorageDescriptor>();

                foreach (string chunkKey in snapshotFile.ChunkHashes ?? [])
                {
                    if (uploadedHashes.TryGetValue(chunkKey, out UploadedHash? found))
                    {
                        chunkDescriptors.Add(ChunkStorageHelpers.Parse(found.Hash, found.CompressionAlgorithm, found.OriginalSize));
                        continue;
                    }

                    _logger.LogWarning("Chunk hash metadata not found in DB: {ChunkKey}", chunkKey);
                    ChunkStorageDescriptor chunk = ChunkStorageHelpers.Parse(chunkKey);
                    chunkDescriptors.Add(await sizeResolver.ResolveAsync(
                        chunk,
                        snapshotFile,
                        cancellationToken));
                }

                result[snapshotFile.Id] = chunkDescriptors;
            }

            return result;
        }
    }
}
