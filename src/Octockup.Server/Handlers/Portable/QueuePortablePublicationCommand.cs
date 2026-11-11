// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using EasyExtensions.Quartz.Extensions;
using Octockup.Server.Jobs;
using Octockup.Server.Portable;
using Quartz;
namespace Octockup.Server.Handlers.Portable
{
    public record QueuePortablePublicationCommand(Guid UserId, Guid ModuleId) : IRequest<PortablePublication>;
    public class QueuePortablePublicationCommandHandler(PortableStorageFactory factory, PortablePublicationStore publications,
        ISchedulerFactory scheduler) : IRequestHandler<QueuePortablePublicationCommand, PortablePublication>
    {
        public async Task<PortablePublication> Handle(QueuePortablePublicationCommand request, CancellationToken ct)
        {
            await factory.OpenAsync(request.UserId, request.ModuleId, ct);
            publications.Set(request.UserId, request.ModuleId, new("queued", 0, 0));
            await scheduler.TriggerJobAsync<PublishPortableCatalogJob>();
            return publications.Get(request.UserId, request.ModuleId);
        }
    }
}
