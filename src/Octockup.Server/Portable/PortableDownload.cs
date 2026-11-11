// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Services;
using Octockup.Server.Archives;
using System.Runtime.CompilerServices;
namespace Octockup.Server.Portable
{
    public class PortableDownload(PortableSessionAccess access, PortableCatalogReader reader)
    {
        public async Task<PortableEntry> FindFileAsync(Guid user, PortableDownloadRequest request, PortableAccess storage, CancellationToken ct)
        {
            string path = PortableFormat.Normalize(request.Path);
            int separator = path.LastIndexOf('/');
            string parent = separator < 0 ? string.Empty : path[..separator];
            PortablePage page = await reader.ReadPageAsync(storage, access.Snapshot(user, request.Session, request.SnapshotId),
                parent, request.Page, ct);
            return page.Entries.SingleOrDefault(x => !x.IsDirectory && x.Path == path)
                ?? throw new AuthApiException(404, "File not found.");
        }
        public Task WriteFolderAsync(Guid user, PortableDownloadRequest request, PortableAccess storage,
            Stream output, CancellationToken ct)
        {
            PortableSnapshot snapshot = access.Snapshot(user, request.Session, request.SnapshotId);
            string path = PortableFormat.Normalize(request.Path);
            return StoredZipArchiveWriter.WriteAsync(output, EntriesAsync(storage, snapshot, path, ct), ct);
        }
        private async IAsyncEnumerable<StoredZipArchiveEntry> EntriesAsync(PortableAccess storage,
            PortableSnapshot snapshot, string path, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (PortableEntry entry in reader.WalkAsync(storage, snapshot, path, ct))
            {
                string relative = path.Length == 0 ? entry.Path : entry.Path[(path.Length + 1)..];
                yield return new(relative, entry.Size, entry.LastModified,
                    token => Task.FromResult(reader.OpenFile(storage, entry, token)));
            }
        }
    }
}
