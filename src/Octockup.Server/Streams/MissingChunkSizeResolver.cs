// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using System.Buffers;
using System.Security.Cryptography;

namespace Octockup.Server.Streams
{
    public class MissingChunkSizeResolver(
        ILogger logger,
        IBackupStorage storage,
        IStreamCipher cipher)
    {
        private const long ReadToEndLength = long.MaxValue;
        private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);

        public async Task<ChunkStorageDescriptor> ResolveAsync(
            ChunkStorageDescriptor chunk,
            SnapshotFile snapshotFile,
            CancellationToken cancellationToken)
        {
            if (chunk.OriginalSize.HasValue)
            {
                return chunk;
            }

            if (!_sizes.TryGetValue(chunk.Key, out long size))
            {
                await using SnapshotConcatStream stream = new(
                    logger,
                    storage,
                    [chunk],
                    snapshotFile,
                    cipher,
                    cancellationToken,
                    ReadToEndLength);
                using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        hasher.AppendData(buffer.AsSpan(0, read));
                        size = checked(size + read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                string actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(actualHash, chunk.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Restored chunk '{chunk.Key}' failed checksum validation.");
                }

                _sizes.Add(chunk.Key, size);
            }

            return chunk with { OriginalSize = size };
        }
    }
}
