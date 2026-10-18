// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;

namespace Octockup.Server.Handlers.Deletion
{
    public record RequestSnapshotDeletionCommand(Guid UserId, Guid SnapshotId) : IRequest<bool>;

    public class RequestSnapshotDeletionCommandHandler(AppDbContext dbContext)
        : IRequestHandler<RequestSnapshotDeletionCommand, bool>
    {
        public async Task<bool> Handle(RequestSnapshotDeletionCommand request, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            int affected = await dbContext.Snapshots
                .Where(x => x.Id == request.SnapshotId && x.Backup.Source.UserId == request.UserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.DeletionRequestedAt, x => x.DeletionRequestedAt ?? now)
                    .SetProperty(x => x.DeletionError, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            return affected > 0;
        }
    }
}
