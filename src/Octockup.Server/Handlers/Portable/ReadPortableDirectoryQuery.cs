// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Octockup.Server.Portable;
namespace Octockup.Server.Handlers.Portable
{
    public record ReadPortableDirectoryQuery(Guid UserId, string Session, Guid SnapshotId, string Path, int Page)
        : IRequest<PortablePage>;
    public class ReadPortableDirectoryQueryHandler(PortableSessionAccess access, PortableCatalogReader reader)
        : IRequestHandler<ReadPortableDirectoryQuery, PortablePage>
    {
        public async Task<PortablePage> Handle(ReadPortableDirectoryQuery request, CancellationToken ct)
        {
            PortableAccess storage = await access.OpenAsync(request.UserId, request.Session, ct);
            PortableSnapshot snapshot = access.Snapshot(request.UserId, request.Session, request.SnapshotId);
            return await reader.ReadPageAsync(storage, snapshot, request.Path, request.Page, ct);
        }
    }
}
