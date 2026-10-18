// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Quartz.Attributes;
using Octockup.Server.Handlers.Administration;
using Quartz;

namespace Octockup.Server.Jobs
{
    [JobTrigger(hours: 1)]
    public class ScheduleStorageCleanupJob(IMediator mediator) : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            Guid[] storageIds = await mediator.Send(
                new GetDueStorageCleanupsQuery(), context.CancellationToken);
            foreach (Guid storageId in storageIds)
            {
                await mediator.Send(new StartStorageCleanupCommand(storageId), context.CancellationToken);
            }
        }
    }
}
