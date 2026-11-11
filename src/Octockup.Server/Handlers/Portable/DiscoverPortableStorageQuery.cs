// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Portable;
using Octockup.Server.Abstractions;
namespace Octockup.Server.Handlers.Portable
{
    public record DiscoverPortableStorageQuery(Guid UserId, Guid ModuleId) : IRequest<PortableDiscovery>;
    public class DiscoverPortableStorageQueryHandler(PortableStorageFactory factory, AppDbContext db,
        PortablePublicationStore publications) : IRequestHandler<DiscoverPortableStorageQuery, PortableDiscovery>
    {
        public async Task<PortableDiscovery> Handle(DiscoverPortableStorageQuery request, CancellationToken ct)
        {
            IBackupStorage storage = await factory.OpenAsync(request.UserId, request.ModuleId, ct);
            bool found = await storage.ExistsAsync(PortableFormat.CatalogPath, ct) == true;
            bool canPublish = await db.Snapshots.AnyAsync(x => x.Backup.StorageId == request.ModuleId &&
                x.Backup.Source.UserId == request.UserId && x.CompletedAt != null &&
                x.DeletionRequestedAt == null && x.Backup.DeletionRequestedAt == null, ct);
            return new(found, canPublish, publications.Get(request.UserId, request.ModuleId));
        }
    }
}
