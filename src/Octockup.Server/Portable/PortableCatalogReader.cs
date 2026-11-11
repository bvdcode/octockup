// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Streams;
using System.Runtime.CompilerServices;
namespace Octockup.Server.Portable
{
    public class PortableCatalogReader(ILogger<PortableCatalogReader> logger)
    {
        public async Task<PortablePage> ReadPageAsync(PortableAccess access, PortableSnapshot snapshot,
            string path, int page, CancellationToken ct)
        {
            path = PortableFormat.Normalize(path);
            string prefix = PortableFormat.DirectoryPath(snapshot.Generation, path);
            PortableDirectory directory = await PortableMetadata.ReadAsync<PortableDirectory>(
                access.Storage, access.Cipher, prefix + "/directory", ct);
            if (page < 0 || page >= directory.Pages)
            {
                throw new InvalidDataException("Catalog page is out of range.");
            }
            List<PortableEntry> entries = await PortableMetadata.ReadAsync<List<PortableEntry>>(
                access.Storage, access.Cipher, $"{prefix}/{page}", ct);
            if (entries.Count > PortableFormat.PageSize)
            {
                throw new InvalidDataException("Catalog page is too large.");
            }
            foreach (PortableEntry entry in entries)
            {
                string expected = path.Length == 0 ? entry.Name : path + "/" + entry.Name;
                if (entry.Name.Length == 0 || entry.Name.Contains('/') || entry.Name.Contains('\\') ||
                    entry.Path != expected || PortableFormat.Normalize(entry.Path) != entry.Path ||
                    entry.Size < 0 || entry.Path.Count(x => x == '/') > 256)
                {
                    throw new InvalidDataException("Invalid catalog path.");
                }
                foreach (Octockup.Server.Helpers.ChunkStorageDescriptor chunk in entry.Chunks)
                {
                    Octockup.Server.Helpers.ChunkKeyIdentity.Parse(chunk.Key);
                }
            }
            return new(entries, page, directory.Pages, directory.Entries);
        }

        public async IAsyncEnumerable<PortableEntry> WalkAsync(PortableAccess access, PortableSnapshot snapshot,
            string path, [EnumeratorCancellation] CancellationToken ct)
        {
            int page = 0;
            PortablePage data;
            do
            {
                data = await ReadPageAsync(access, snapshot, path, page++, ct);
                foreach (PortableEntry entry in data.Entries)
                {
                    if (entry.IsDirectory)
                    {
                        await foreach (PortableEntry file in WalkAsync(access, snapshot, entry.Path, ct))
                        {
                            yield return file;
                        }
                    }
                    else
                    {
                        yield return entry;
                    }
                }
            } while (page < data.Pages);
        }

        public Stream OpenFile(PortableAccess access, PortableEntry entry, CancellationToken ct)
        {
            SnapshotFile file = new()
            {
                Path = entry.Path, Name = entry.Name, Size = entry.Size, LastModified = entry.LastModified,
                Hashsum = entry.Hashsum ?? throw new InvalidDataException("File checksum is missing."),
                ChunkHashes = entry.Chunks.Select(x => x.Key).ToArray(),
            };
            return new SnapshotConcatStream(logger, access.Storage, entry.Chunks, file,
                access.Cipher, ct, entry.Size, validate: true);
        }
    }
}
