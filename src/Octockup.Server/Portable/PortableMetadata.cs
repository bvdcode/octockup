// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Models;
using System.Text.Json;
namespace Octockup.Server.Portable
{
    public static class PortableMetadata
    {
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        public static async Task<PortableIndex> ReadIndexAsync(IBackupStorage storage, CancellationToken ct)
        {
            using MemoryStream stream = new();
            await ReadBoundedAsync(storage, PortableFormat.CatalogPath, stream, ct);
            PortableIndex index = await JsonSerializer.DeserializeAsync<PortableIndex>(stream, ReadOptions, ct)
                ?? throw new InvalidDataException("Catalog index is empty.");
            if (index.Version != PortableFormat.Version || index.Backups.Count > 10_000 ||
                index.Backups.Contains(Guid.Empty) || index.Backups.Distinct().Count() != index.Backups.Count)
            {
                throw new InvalidDataException("Unsupported catalog index.");
            }
            return index;
        }
        public static async Task WriteIndexAsync(IBackupStorage storage, PortableIndex index, CancellationToken ct)
        {
            using MemoryStream stream = new();
            await JsonSerializer.SerializeAsync(stream, index, cancellationToken: ct);
            stream.Position = 0;
            await storage.UploadAsync(PortableFormat.CatalogPath, stream, ct);
        }
        public static async Task<T> ReadAsync<T>(IBackupStorage storage, IStreamCipher cipher, string path, CancellationToken ct)
        {
            using MemoryStream encrypted = new();
            await ReadBoundedAsync(storage, path, encrypted, ct);
            await using Stream plaintext = await cipher.DecryptAsync(encrypted, ct: ct);
            return await JsonSerializer.DeserializeAsync<T>(plaintext, ReadOptions, ct)
                ?? throw new InvalidDataException("Catalog object is empty.");
        }

        private static async Task ReadBoundedAsync(IBackupStorage storage, string path, MemoryStream encrypted, CancellationToken ct)
        {
            BackupFileInfo? info = await storage.GetFileInfoAsync(path, ct);
            if (info is null)
            {
                throw new InvalidDataException("Catalog object is missing.");
            }
            if (info.Size is null || info.Size > PortableFormat.MaximumMetadataBytes || info.Size < 0)
            {
                throw new InvalidDataException("Catalog object is too large.");
            }
            await using Stream input = await storage.GetFileStreamAsync(info, ct);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (encrypted.Length + read > PortableFormat.MaximumMetadataBytes)
                {
                    throw new InvalidDataException("Catalog object is too large.");
                }
                await encrypted.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            encrypted.Position = 0;
        }

        public static async Task WriteAsync<T>(IBackupStorage storage, IStreamCipher cipher, string path, T value, CancellationToken ct)
        {
            using MemoryStream plaintext = new();
            await JsonSerializer.SerializeAsync(plaintext, value, cancellationToken: ct);
            if (plaintext.Length > PortableFormat.MaximumMetadataBytes / 2)
            {
                throw new InvalidDataException("Catalog page is too large.");
            }
            plaintext.Position = 0;
            using MemoryStream encrypted = new();
            await cipher.EncryptAsync(plaintext, encrypted, ct: ct);
            encrypted.Position = 0;
            await storage.UploadAsync(path, encrypted, ct);
        }
    }
}
