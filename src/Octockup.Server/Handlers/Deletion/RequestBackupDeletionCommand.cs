// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Jobs;

namespace Octockup.Server.Handlers.Deletion
{
    public record RequestBackupDeletionCommand(Guid UserId, Guid BackupId) : IRequest<bool>;

    public class RequestBackupDeletionCommandHandler(AppDbContext dbContext)
        : IRequestHandler<RequestBackupDeletionCommand, bool>
    {
        public async Task<bool> Handle(RequestBackupDeletionCommand request, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            int affected = await dbContext.Backups
                .Where(x => x.Id == request.BackupId && x.Source.UserId == request.UserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.DeletionRequestedAt, x => x.DeletionRequestedAt ?? now)
                    .SetProperty(x => x.DeletionError, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (affected == 0)
            {
                return false;
            }

            Guid[] scheduleIds = await dbContext.Schedules.Where(x => x.BackupId == request.BackupId)
                .Select(x => x.Id).ToArrayAsync(cancellationToken);
            foreach (Guid scheduleId in scheduleIds)
            {
                ExecuteBackupJob.StopRunningBackup(scheduleId);
            }
            return true;
        }
    }
}
