// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Models.Enums;

namespace Octockup.Server.Handlers.Administration
{
    public record GetDueStorageCleanupsQuery : IRequest<Guid[]>;

    public class GetDueStorageCleanupsQueryHandler(AppDbContext dbContext)
        : IRequestHandler<GetDueStorageCleanupsQuery, Guid[]>
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(30);

        public Task<Guid[]> Handle(GetDueStorageCleanupsQuery request, CancellationToken cancellationToken)
        {
            DateTime cutoff = DateTime.UtcNow - CleanupInterval;
            var dueStorages = from module in dbContext.Modules
                              join cleanup in dbContext.StorageCleanups
                                  on module.Id equals cleanup.ModuleId into cleanups
                              from cleanup in cleanups.DefaultIfEmpty()
                              where module.Destination == ModuleDestination.Target
                                  && (cleanup == null || cleanup.Status != StorageCleanupStatus.Running)
                                  && (cleanup == null
                                      ? module.CreatedAt <= cutoff
                                      : (cleanup.LastStartedAt ?? module.CreatedAt) <= cutoff)
                              select module.Id;
            return dueStorages.ToArrayAsync(cancellationToken);
        }
    }
}
