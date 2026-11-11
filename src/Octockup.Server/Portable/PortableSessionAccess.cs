// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Crypto;
using Octockup.Server.Services;
namespace Octockup.Server.Portable
{
    public class PortableSessionAccess(PortableSessionStore sessions, PortableStorageFactory factory)
    {
        public async Task<PortableAccess> OpenAsync(Guid userId, string token, CancellationToken ct)
        {
            PortableSession session = sessions.Get(userId, token);
            return new(await factory.OpenAsync(userId, session.ModuleId, ct), session.ModuleId,
                new AesGcmStreamCipher(session.Key.ToArray()));
        }
        public PortableSnapshot Snapshot(Guid userId, string token, Guid snapshotId) =>
            sessions.Get(userId, token).Catalog.Snapshots.SingleOrDefault(x => x.Id == snapshotId)
                ?? throw new AuthApiException(404, "Snapshot not found.");
    }
}
