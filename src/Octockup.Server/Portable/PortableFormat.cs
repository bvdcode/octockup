// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Security.Cryptography;
using System.Text;
namespace Octockup.Server.Portable
{
    public static class PortableFormat
    {
        public const string Root = ".octockup/v1";
        public const string CatalogPath = Root + "/catalog";
        public const int PageSize = 500;
        public const int Version = 1;
        public const int MaximumSnapshots = 10_000;
        public static string BackupCatalog(Guid backupId) => $"{Root}/catalog-{backupId:N}";
        public static string PendingGeneration(Guid generation) => $"{Root}/{generation:N}/pending";
        public const int MaximumMetadataBytes = 64 * 1024 * 1024;
        public static string DirectoryId(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        public static string DirectoryPath(Guid generation, string path) => $"{Root}/{generation:N}/{DirectoryId(path)}";
        public static string Normalize(string path)
        {
            string value = path.Replace('\\', '/').Trim('/');
            if (value.Split('/').Any(x => x is "." or "..") || value.Contains('\0'))
            {
                throw new InvalidDataException("Invalid catalog path.");
            }
            return value;
        }
    }
}
