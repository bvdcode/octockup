// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Octockup.Server.Archives
{
    public static partial class StoredZipArchiveWriter
    {
        public static async Task WriteAsync(Stream output, IAsyncEnumerable<StoredZipArchiveEntry> entries,
            CancellationToken cancellationToken = default)
        {
            string spool = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            await using FileStream central = new(spool, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            long written = 0;
            int count = 0;
            await foreach (StoredZipArchiveEntry entry in entries.WithCancellation(cancellationToken))
            {
                ArchiveRecord record = CreateRecord(entry);
                record.LocalHeaderOffset = written;
                await WriteLocalHeaderAsync(output, record, cancellationToken);
                written += LocalHeaderSize + record.NameBytes.Length + LocalZip64ExtraSize;
                Crc32 crc = new();
                long copied = await CopyEntryAsync(output, record, crc, cancellationToken);
                if (copied != entry.Size)
                {
                    throw new InvalidDataException("ZIP entry length does not match its catalog.");
                }
                record.Crc32 = crc.Value;
                written += copied;
                await WriteDataDescriptorAsync(output, record, cancellationToken);
                written += DataDescriptorZip64Size;
                await WriteCentralDirectoryHeaderAsync(central, record, cancellationToken);
                count = checked(count + 1);
            }
            long centralSize = central.Length;
            long centralOffset = written;
            central.Position = 0;
            await central.CopyToAsync(output, cancellationToken);
            written += centralSize;
            await WriteZip64EndOfCentralDirectoryAsync(output, count, centralSize, centralOffset, cancellationToken);
            await WriteZip64EndOfCentralDirectoryLocatorAsync(output, written, cancellationToken);
            await WriteEndOfCentralDirectoryAsync(output, cancellationToken);
        }
    }
}
