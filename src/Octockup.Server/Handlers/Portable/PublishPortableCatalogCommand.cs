// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Octockup.Server.Portable;
namespace Octockup.Server.Handlers.Portable
{
    public record PublishPortableCatalogCommand(Guid UserId, Guid ModuleId, bool CustomKeysOnly = false) : IRequest<bool>;
    public class PublishPortableCatalogCommandHandler(PortableCatalogPublisher publisher)
        : IRequestHandler<PublishPortableCatalogCommand, bool>
    {
        public async Task<bool> Handle(PublishPortableCatalogCommand request, CancellationToken ct)
        {
            await publisher.PublishAsync(request.UserId, request.ModuleId, ct, request.CustomKeysOnly);
            return true;
        }
    }
}
