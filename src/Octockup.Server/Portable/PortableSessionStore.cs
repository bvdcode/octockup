// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.Extensions.Caching.Memory;
using Octockup.Server.Services;
using System.Security.Cryptography;
namespace Octockup.Server.Portable
{
    public class PortableSessionStore : IDisposable
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 128 });
        public string Add(PortableSession session)
        {
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            MemoryCacheEntryOptions options = new MemoryCacheEntryOptions().SetSize(1)
                .SetAbsoluteExpiration(session.ExpiresAt);
            options.RegisterPostEvictionCallback(static (_, value, _, _) => (value as PortableSession)?.Dispose());
            _cache.Set(token, session, options);
            return token;
        }
        public PortableSession Get(Guid userId, string token)
        {
            if (!_cache.TryGetValue(token, out PortableSession? session) || session is null ||
                session.UserId != userId || session.ExpiresAt <= DateTime.UtcNow)
            {
                throw new AuthApiException(410, "Storage viewing session expired.");
            }
            return session;
        }
        public void Remove(Guid userId, string token)
        {
            Get(userId, token).Dispose();
            _cache.Remove(token);
        }
        public void Dispose() => _cache.Dispose();
    }
}
