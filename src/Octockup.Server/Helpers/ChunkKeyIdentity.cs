// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Enums;

namespace Octockup.Server.Helpers
{
    public readonly record struct ChunkKeyIdentity(
        ulong First,
        ulong Second,
        ulong Third,
        ulong Fourth,
        ChunkKeyVariant Variant)
    {
        private const int HashLength = 64;
        private const int PartLength = 16;
        private const string Version2Prefix = "v2-";

        public static ChunkKeyIdentity Parse(string storageKey)
        {
            ArgumentException.ThrowIfNullOrEmpty(storageKey);
            if (storageKey.StartsWith("v3-", StringComparison.Ordinal))
            {
                ChunkStorageHelpers.Parse(storageKey);
                storageKey = storageKey[36..];
            }

            ChunkStorageDescriptor descriptor = ChunkStorageHelpers.Parse(storageKey);
            ReadOnlySpan<char> contentHash = descriptor.ContentHash.AsSpan();
            if (contentHash.Length != HashLength)
            {
                throw new FormatException("A chunk hash must contain 64 hexadecimal characters.");
            }

            ChunkKeyVariant variant = GetVariant(storageKey, descriptor);
            return new ChunkKeyIdentity(
                ParsePart(contentHash[..PartLength]),
                ParsePart(contentHash.Slice(PartLength, PartLength)),
                ParsePart(contentHash.Slice(PartLength * 2, PartLength)),
                ParsePart(contentHash.Slice(PartLength * 3, PartLength)),
                variant);
        }

        private static ChunkKeyVariant GetVariant(
            string storageKey,
            ChunkStorageDescriptor descriptor)
        {
            if (!storageKey.StartsWith(Version2Prefix, StringComparison.Ordinal))
            {
                return ChunkKeyVariant.Legacy;
            }

            return (descriptor.CompressionAlgorithm, descriptor.IsEncrypted) switch
            {
                (CompressionAlgorithm.None, true) => ChunkKeyVariant.Version2NoneEncrypted,
                (CompressionAlgorithm.None, false) => ChunkKeyVariant.Version2NonePlain,
                (CompressionHelpers.Algorithm, true) => ChunkKeyVariant.Version2ZstdEncrypted,
                (CompressionHelpers.Algorithm, false) => ChunkKeyVariant.Version2ZstdPlain,
                _ => throw new NotSupportedException(
                    $"Unsupported chunk key variant: {storageKey}"),
            };
        }

        private static ulong ParsePart(ReadOnlySpan<char> value)
        {
            ulong result = 0;
            foreach (char character in value)
            {
                result = (result << 4) | ParseNibble(character);
            }

            return result;
        }

        private static uint ParseNibble(char character)
        {
            return character switch
            {
                >= '0' and <= '9' => (uint)(character - '0'),
                >= 'a' and <= 'f' => (uint)(character - 'a' + 10),
                _ => throw new FormatException(
                    "A chunk hash must use lowercase hexadecimal characters."),
            };
        }
    }
}
