// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;
namespace Octockup.Server.Portable
{
    public class PortablePublicationStore
    {
        private readonly ConcurrentDictionary<(Guid User, Guid Module), PortablePublication> _states = new();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
        public SemaphoreSlim GetLock(Guid moduleId) => _locks.GetOrAdd(moduleId, static _ => new SemaphoreSlim(1, 1));
        public PortablePublication Get(Guid user, Guid module) =>
            _states.GetValueOrDefault((user, module)) ?? new("idle", 0, 0);
        public void Set(Guid user, Guid module, PortablePublication state) => _states[(user, module)] = state;
        public (Guid User, Guid Module)[] Queued() => _states.Where(x => x.Value.Status == "queued")
            .Select(x => x.Key).ToArray();
    }
}
