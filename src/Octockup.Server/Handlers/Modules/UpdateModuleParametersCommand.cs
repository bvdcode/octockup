// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Helpers;

namespace Octockup.Server.Handlers.Modules
{
    public record UpdateModuleParametersCommand(Guid UserId, Guid ModuleId,
        IReadOnlyDictionary<string, string> Parameters) : IRequest<bool>;

    public class UpdateModuleParametersCommandHandler(AppDbContext dbContext, IStreamCipher cipher)
        : IRequestHandler<UpdateModuleParametersCommand, bool>
    {
        public async Task<bool> Handle(UpdateModuleParametersCommand request, CancellationToken cancellationToken)
        {
            Module? module = await dbContext.Modules.SingleOrDefaultAsync(
                x => x.Id == request.ModuleId && x.UserId == request.UserId, cancellationToken);
            if (module is null)
            {
                return false;
            }
            Dictionary<string, string> parameters = ModuleParameterValues.Merge(module.Params(cipher).Snapshot(), request.Parameters);
            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                module.Params(cipher)[parameter.Key] = parameter.Value;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
