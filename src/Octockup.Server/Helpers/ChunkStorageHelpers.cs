// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Enums;

namespace Octockup.Server.Helpers
{
    public static class ChunkStorageHelpers
    {
        private const string Version = "v2";
        private const string CompressionNone = "none";
        private const string CompressionZstd = "zstd";
        private const string EncryptionEnabled = "enc";
        private const string EncryptionDisabled = "plain";

        public static string CreateKey(
            string contentHash,
            CompressionAlgorithm compressionAlgorithm,
            bool isEncrypted)
        {
            if (compressionAlgorithm == CompressionHelpers.Algorithm && isEncrypted)
            {
                return contentHash;
            }

            string compression = ToCompressionCode(compressionAlgorithm);
            string encryption = isEncrypted ? EncryptionEnabled : EncryptionDisabled;
            return $"{Version}-{compression}-{encryption}-{contentHash}";
        }

        public static ChunkStorageDescriptor Parse(
            string key,
            CompressionAlgorithm? legacyCompressionAlgorithm = null,
            long? originalSize = null)
        {
            if (key.StartsWith("v3-", StringComparison.Ordinal))
            {
                if (key.Length < 37 || !Guid.TryParseExact(key.Substring(3, 32), "N", out _) || key[35] != '-' ||
                    key.AsSpan(36).StartsWith("v3-", StringComparison.Ordinal))
                {
                    throw new FormatException("Invalid scoped chunk key.");
                }
                return Parse(key[36..], legacyCompressionAlgorithm, originalSize) with { Key = key };
            }
            if (!key.StartsWith(Version + "-", StringComparison.Ordinal))
            {
                return new ChunkStorageDescriptor(
                    key,
                    key,
                    legacyCompressionAlgorithm ?? CompressionHelpers.Algorithm,
                    IsEncrypted: true,
                    originalSize);
            }

            string[] parts = key.Split('-', 4);
            if (parts.Length != 4 || parts[0] != Version)
            {
                throw new FormatException($"Unsupported chunk key format: {key}");
            }

            return new ChunkStorageDescriptor(
                key,
                parts[3],
                FromCompressionCode(parts[1]),
                FromEncryptionCode(parts[2]),
                originalSize);
        }

        public static string GetStoragePath(string key, char pathSeparator)
        {
            ChunkStorageDescriptor descriptor = Parse(key);
            if (key.StartsWith("v3-", StringComparison.Ordinal))
            {
                return string.Join(pathSeparator, ".octockup", "data", key.Substring(3, 32),
                    GetStoragePath(key[36..], pathSeparator));
            }
            if (descriptor.Key == descriptor.ContentHash)
            {
                return ScheduleHelpers.SplitPlainHash(descriptor.ContentHash, pathSeparator);
            }

            string compression = ToCompressionCode(descriptor.CompressionAlgorithm);
            string encryption = descriptor.IsEncrypted ? EncryptionEnabled : EncryptionDisabled;
            string hash = descriptor.ContentHash;
            return string.Join(
                pathSeparator,
                Version,
                compression,
                encryption,
                hash[..2],
                hash.Substring(2, 2),
                hash[4..] + "." + CompressionHelpers.Extension);
        }

        private static string ToCompressionCode(CompressionAlgorithm algorithm)
        {
            return algorithm switch
            {
                CompressionAlgorithm.None => CompressionNone,
                CompressionHelpers.Algorithm => CompressionZstd,
                _ => throw new NotSupportedException($"Unsupported compression algorithm: {algorithm}"),
            };
        }

        private static CompressionAlgorithm FromCompressionCode(string code)
        {
            return code switch
            {
                CompressionNone => CompressionAlgorithm.None,
                CompressionZstd => CompressionHelpers.Algorithm,
                _ => throw new NotSupportedException($"Unsupported compression algorithm code: {code}"),
            };
        }

        private static bool FromEncryptionCode(string code)
        {
            return code switch
            {
                EncryptionEnabled => true,
                EncryptionDisabled => false,
                _ => throw new NotSupportedException($"Unsupported encryption code: {code}"),
            };
        }
    }
}
