// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Quartz.Attributes;
using Octockup.Server.Handlers.Deletion;
using Quartz;

namespace Octockup.Server.Jobs
{
    [JobTrigger(seconds: 10)]
    public class DeleteBackupMetadataJob(IMediator mediator) : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            await mediator.Send(new ProcessPendingDeletionsCommand(), context.CancellationToken);
        }
    }
}
