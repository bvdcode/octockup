// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;

namespace Octockup.Server.Handlers.Modules
{
    public record PrepareModuleConnectionQuery(Guid UserId, Guid ModuleId,
        IReadOnlyDictionary<string, string> Parameters) : IRequest<PreparedModuleConnection?>;

    public class PrepareModuleConnectionQueryHandler(AppDbContext dbContext, IStreamCipher cipher,
        IEnumerable<IBackupProvider> providers) : IRequestHandler<PrepareModuleConnectionQuery, PreparedModuleConnection?>
    {
        public async Task<PreparedModuleConnection?> Handle(PrepareModuleConnectionQuery request, CancellationToken cancellationToken)
        {
            Module? module = await dbContext.Modules.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.ModuleId && x.UserId == request.UserId, cancellationToken);
            if (module is null)
            {
                return null;
            }
            IBackupSource provider = providers.OfType<IBackupSource>().Single(x => x.Id == module.BackupModuleId);
            provider.SetParameters(ModuleParameterValues.Merge(module.Params(cipher).Snapshot(), request.Parameters));
            return new(provider, module.Destination);
        }
    }
}
