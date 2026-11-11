// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Models.Enums;
using Octockup.Server.Services;
namespace Octockup.Server.Portable
{
    public class PortableStorageFactory(AppDbContext db, IStreamCipher cipher,
        IEnumerable<IBackupProvider> providers)
    {
        public async Task<IBackupStorage> OpenAsync(Guid userId, Guid moduleId, CancellationToken ct)
        {
            Module? module = await db.Modules.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == moduleId && x.UserId == userId && x.Destination == ModuleDestination.Target, ct);
            if (module is null)
            {
                throw new AuthApiException(404, "Storage not found.");
            }
            IBackupProvider? prototype = providers.FirstOrDefault(x => x.Id == module.BackupModuleId);
            if (prototype is null)
            {
                throw new AuthApiException(400, "Storage provider is unavailable.");
            }
            IBackupProvider provider = prototype;
            provider.SetParameters(module.Params(cipher).Snapshot());
            if (provider is not IBackupStorage storage)
            {
                throw new AuthApiException(400, "Provider is not a storage.");
            }
            return storage;
        }
    }
}
