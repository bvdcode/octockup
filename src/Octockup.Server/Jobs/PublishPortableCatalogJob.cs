// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Quartz.Attributes;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Portable;
using Octockup.Server.Portable;
using Quartz;
namespace Octockup.Server.Jobs
{
    [JobTrigger(minutes: 10, startNow: false)]
    public class PublishPortableCatalogJob(AppDbContext db, IMediator mediator, PortablePublicationStore publications,
        ILogger<PublishPortableCatalogJob> logger) : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            var local = await db.Backups.AsNoTracking().Where(x => x.DeletionRequestedAt == null && x.EncryptedMasterKey != null &&
                x.Snapshots.Any(s => s.CompletedAt != null && s.DeletionRequestedAt == null))
                .Select(x => new { User = x.Source.UserId, Module = x.StorageId }).Distinct().ToListAsync(context.CancellationToken);
            (Guid User, Guid Module)[] queued = publications.Queued();
            var targets = local.Select(x => (x.User, x.Module)).Concat(queued).Distinct();
            foreach ((Guid User, Guid Module) target in targets)
            {
                try
                {
                    await mediator.Send(new PublishPortableCatalogCommand(target.User, target.Module,
                        CustomKeysOnly: !queued.Contains(target)), context.CancellationToken);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Catalog publication failed for storage {ModuleId}", target.Module);
                    publications.Set(target.User, target.Module, new("failed", 0, 0));
                }
            }
        }
    }
}
