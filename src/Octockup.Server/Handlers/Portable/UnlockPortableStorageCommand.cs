// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Crypto;
using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Octockup.Server.Portable;
using Octockup.Server.Services;
using System.Security.Cryptography;
using Octockup.Server.Abstractions;
namespace Octockup.Server.Handlers.Portable
{
    public record UnlockPortableStorageCommand(Guid UserId, Guid ModuleId, string MasterKey) : IRequest<PortableUnlockResult>;
    public class UnlockPortableStorageCommandHandler(PortableStorageFactory factory, PortableSessionStore sessions,
        ILogger<UnlockPortableStorageCommandHandler> logger)
        : IRequestHandler<UnlockPortableStorageCommand, PortableUnlockResult>
    {
        public async Task<PortableUnlockResult> Handle(UnlockPortableStorageCommand request, CancellationToken ct)
        {
            IBackupStorage storage = await factory.OpenAsync(request.UserId, request.ModuleId, ct);
            if (string.IsNullOrWhiteSpace(request.MasterKey) || request.MasterKey.Length > 4096)
            {
                throw new AuthApiException(400, "Enter a valid master key.");
            }
            byte[] key = KeyDerivation.DeriveSubkey(request.MasterKey.Trim(), "crypto", 32);
            bool retained = false;
            try
            {
                PortableIndex index = await PortableMetadata.ReadIndexAsync(storage, ct);
                PortableCatalog catalog = new(PortableFormat.Version, []);
                foreach (Guid backupId in index.Backups)
                {
                    try
                    {
                        PortableCatalog backup = await PortableMetadata.ReadAsync<PortableCatalog>(storage,
                            new AesGcmStreamCipher(key), PortableFormat.BackupCatalog(backupId), ct);
                        if (backup.Version != PortableFormat.Version)
                        {
                            throw new InvalidDataException("Unsupported catalog version.");
                        }
                        if (catalog.Snapshots.Count + backup.Snapshots.Count > PortableFormat.MaximumSnapshots)
                        {
                            throw new InvalidDataException("The catalog contains too many snapshots for one viewing session.");
                        }
                        catalog.Snapshots.AddRange(backup.Snapshots);
                    }
                    catch (CryptographicException exception)
                    {
                        logger.LogDebug(exception, "Catalog {BackupId} uses another key", backupId);
                    }
                }
                if (catalog.Snapshots.Count == 0)
                {
                    throw new CryptographicException("No snapshots match this key.");
                }
                DateTime expiresAt = DateTime.UtcNow + PortableSessionStore.Lifetime;
                PortableSession session = new(request.UserId, request.ModuleId, key, catalog, expiresAt);
                string token = sessions.Add(session);
                retained = true;
                return new(token, expiresAt, catalog.Snapshots);
            }
            catch (Exception exception) when (exception is CryptographicException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogWarning(exception, "Could not unlock portable catalog for storage {ModuleId}", request.ModuleId);
                throw new AuthApiException(400, "The master key is incorrect or the catalog is damaged.");
            }
            finally
            {
                if (!retained)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
    }
}
