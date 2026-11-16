// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models.Dto;

namespace Octockup.Server.Handlers.Modules
{
    public record GetModuleParametersQuery(Guid UserId, Guid ModuleId) : IRequest<ModuleParametersDto?>;

    public class GetModuleParametersQueryHandler(AppDbContext dbContext, IStreamCipher cipher)
        : IRequestHandler<GetModuleParametersQuery, ModuleParametersDto?>
    {
        public async Task<ModuleParametersDto?> Handle(GetModuleParametersQuery request, CancellationToken cancellationToken)
        {
            Module? module = await dbContext.Modules.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.ModuleId && x.UserId == request.UserId, cancellationToken);
            if (module is null)
            {
                return null;
            }
            IReadOnlyDictionary<string, string> parameters = module.Params(cipher).Snapshot();
            return new()
            {
                Id = module.Id,
                Parameters = parameters.Where(x => !ModuleParameterValues.IsSecret(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value),
                StoredSecrets = parameters.Where(x => ModuleParameterValues.IsSecret(x.Key) && x.Value.Length > 0)
                    .Select(x => x.Key).ToArray(),
            };
        }
    }
}
