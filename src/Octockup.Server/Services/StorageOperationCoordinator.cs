// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using System.Collections.Concurrent;

namespace Octockup.Server.Services
{
    public class StorageOperationCoordinator
    {
        private readonly ConcurrentDictionary<Guid, StorageOperationLock> _locks = new();
        private readonly ConcurrentDictionary<Guid, StorageOperationLock> _snapshotLocks = new();

        public Task<StorageOperationLease> AcquireSnapshotUseAsync(Guid backupId, CancellationToken cancellationToken)
        {
            return GetSnapshotLock(backupId).AcquireBackupAsync(cancellationToken);
        }

        public StorageOperationLease? TryAcquireSnapshotDeletion(Guid backupId)
        {
            return GetSnapshotLock(backupId).TryAcquireCleanup();
        }

        private StorageOperationLock GetSnapshotLock(Guid backupId)
        {
            return _snapshotLocks.GetOrAdd(backupId, static _ => new StorageOperationLock());
        }

        public Task<StorageOperationLease> AcquireBackupAsync(
            Guid storageId,
            CancellationToken cancellationToken)
        {
            return GetLock(storageId).AcquireBackupAsync(cancellationToken);
        }

        public StorageOperationLease? TryAcquireCleanup(Guid storageId)
        {
            return GetLock(storageId).TryAcquireCleanup();
        }

        private StorageOperationLock GetLock(Guid storageId)
        {
            return _locks.GetOrAdd(storageId, static _ => new StorageOperationLock());
        }
    }
}
