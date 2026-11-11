// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Octockup.Server.Handlers.Portable;
using Octockup.Server.Portable;
using Octockup.Server.Archives;
using Octockup.Server.Helpers;
using EasyExtensions;
using Octockup.Server.Services;
namespace Octockup.Server.Controllers
{
    [ApiController]
    [Authorize]
    [Route("/api/v1/storage-browser")]
    public class StorageBrowserController(IMediator mediator, PortableSessionStore sessions,
        PortableSessionAccess access, PortableCatalogReader reader, PortableDownload downloads,
        StorageOperationCoordinator operations) : ControllerBase
    {
        [HttpGet("{moduleId:guid}")]
        public async Task<IActionResult> Discover(Guid moduleId, CancellationToken ct) =>
            Ok(await mediator.Send(new DiscoverPortableStorageQuery(User.GetUserId(), moduleId), ct));

        [HttpPost("{moduleId:guid}/publish")]
        public async Task<IActionResult> Publish(Guid moduleId, CancellationToken ct) =>
            Accepted(await mediator.Send(new QueuePortablePublicationCommand(User.GetUserId(), moduleId), ct));

        [HttpPost("{moduleId:guid}/unlock")]
        public async Task<IActionResult> Unlock(Guid moduleId, PortableUnlockRequest request, CancellationToken ct)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(await mediator.Send(new UnlockPortableStorageCommand(User.GetUserId(), moduleId, request.MasterKey), ct));
        }

        [HttpDelete("session")]
        public IActionResult Close([FromHeader(Name = "X-Storage-Session")] string session)
        {
            sessions.Remove(User.GetUserId(), session);
            return NoContent();
        }

        [HttpGet("directory")]
        public async Task<IActionResult> Directory([FromHeader(Name = "X-Storage-Session")] string session,
            Guid snapshotId, string path = "", int page = 0, CancellationToken ct = default)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(await mediator.Send(new ReadPortableDirectoryQuery(User.GetUserId(), session, snapshotId, path, page), ct));
        }

        [HttpPost("download")]
        public async Task<IActionResult> Download([FromForm] PortableDownloadRequest request, CancellationToken ct)
        {
            Guid user = User.GetUserId();
            PortableAccess storage = await access.OpenAsync(user, request.Session, ct);
            PortableSnapshot snapshot = access.Snapshot(user, request.Session, request.SnapshotId);
            StorageOperationLease snapshotLease = await operations.AcquireSnapshotUseAsync(snapshot.BackupId, ct);
            Response.OnCompleted(() => snapshotLease.DisposeAsync().AsTask());
            StorageOperationLease lease = await operations.AcquireBackupAsync(storage.ModuleId, ct);
            Response.OnCompleted(() => lease.DisposeAsync().AsTask());
            Response.Headers.CacheControl = "private, no-store, no-transform";
            if (request.Folder)
            {
                Response.ContentType = "application/zip";
                Response.Headers.ContentDisposition = SnapshotArchiveFileName.CreateContentDisposition("backup.zip");
                await downloads.WriteFolderAsync(user, request, storage, Response.Body, ct);
                return new EmptyResult();
            }
            PortableEntry entry = await downloads.FindFileAsync(user, request, storage, ct);
            Response.ContentLength = entry.Size;
            return File(reader.OpenFile(storage, entry, ct), "application/octet-stream", entry.Name);
        }
    }
}
