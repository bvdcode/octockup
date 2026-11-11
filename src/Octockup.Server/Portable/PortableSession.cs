// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Portable
{
    public class PortableSession(Guid userId, Guid moduleId, byte[] key, PortableCatalog catalog, DateTime expiresAt) : IDisposable
    {
        public Guid UserId { get; } = userId;
        public Guid ModuleId { get; } = moduleId;
        public byte[] Key { get; } = key;
        public PortableCatalog Catalog { get; } = catalog;
        public DateTime ExpiresAt { get; } = expiresAt;
        public void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(Key);
    }
}
