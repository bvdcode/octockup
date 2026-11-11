// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using System.Buffers.Binary;
using System.Text;

namespace Octockup.Server.Archives
{
    public static partial class StoredZipArchiveWriter
    {
        private const ushort VersionNeededZip64 = 45;
        private const ushort GeneralPurposeFlags = 0x0808; // data descriptor + UTF-8
        private const ushort StoreMethod = 0;
        private const ushort Zip64ExtraFieldId = 0x0001;

        private const int LocalHeaderSize = 30;
        private const int LocalZip64ExtraSize = 20;
        private const int DataDescriptorZip64Size = 24;
        private const int CentralDirectoryHeaderSize = 46;
        private const int CentralDirectoryZip64ExtraSize = 28;
        private const int Zip64EndOfCentralDirectorySize = 56;
        private const int Zip64EndOfCentralDirectoryLocatorSize = 20;
        private const int EndOfCentralDirectorySize = 22;

        public static long CalculateContentLength(IReadOnlyCollection<StoredZipArchiveEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            checked
            {
                long total = 0;
                long centralDirectorySize = 0;

                foreach (StoredZipArchiveEntry entry in entries)
                {
                    byte[] nameBytes = GetNameBytes(entry.Name);
                    ValidateEntrySize(entry);

                    total += LocalHeaderSize + nameBytes.Length + LocalZip64ExtraSize;
                    total += entry.Size;
                    total += DataDescriptorZip64Size;
                    centralDirectorySize += CentralDirectoryHeaderSize + nameBytes.Length + CentralDirectoryZip64ExtraSize;
                }

                total += centralDirectorySize;
                total += Zip64EndOfCentralDirectorySize;
                total += Zip64EndOfCentralDirectoryLocatorSize;
                total += EndOfCentralDirectorySize;
                return total;
            }
        }

        public static string NormalizeEntryName(string path, string fallbackName)
        {
            string source = string.IsNullOrWhiteSpace(path) ? fallbackName : path;
            string[] segments = source
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSegment)
                .Where(x => x.Length > 0)
                .ToArray();

            if (segments.Length == 0)
            {
                return NormalizeSegment(fallbackName);
            }

            return string.Join('/', segments);
        }

        public static async Task WriteAsync(
            Stream output,
            IReadOnlyCollection<StoredZipArchiveEntry> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(entries);

            List<ArchiveRecord> records = entries
                .Select(CreateRecord)
                .ToList();

            long written = 0;

            foreach (ArchiveRecord? record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                record.LocalHeaderOffset = written;
                await WriteLocalHeaderAsync(output, record, cancellationToken).ConfigureAwait(false);
                written += LocalHeaderSize + record.NameBytes.Length + LocalZip64ExtraSize;

                Crc32 crc = new Crc32();
                long copied = await CopyEntryAsync(output, record, crc, cancellationToken).ConfigureAwait(false);
                if (copied != record.Entry.Size)
                {
                    throw new InvalidDataException(
                        $"ZIP entry '{record.Entry.Name}' was expected to stream {record.Entry.Size} bytes, but streamed {copied} bytes.");
                }

                record.Crc32 = crc.Value;
                written += copied;

                await WriteDataDescriptorAsync(output, record, cancellationToken).ConfigureAwait(false);
                written += DataDescriptorZip64Size;
            }

            long centralDirectoryOffset = written;
            foreach (ArchiveRecord? record in records)
            {
                await WriteCentralDirectoryHeaderAsync(output, record, cancellationToken).ConfigureAwait(false);
                written += CentralDirectoryHeaderSize + record.NameBytes.Length + CentralDirectoryZip64ExtraSize;
            }

            long centralDirectorySize = written - centralDirectoryOffset;
            long zip64EndOfCentralDirectoryOffset = written;

            await WriteZip64EndOfCentralDirectoryAsync(
                output,
                records.Count,
                centralDirectorySize,
                centralDirectoryOffset,
                cancellationToken).ConfigureAwait(false);
            written += Zip64EndOfCentralDirectorySize;

            await WriteZip64EndOfCentralDirectoryLocatorAsync(
                output,
                zip64EndOfCentralDirectoryOffset,
                cancellationToken).ConfigureAwait(false);
            written += Zip64EndOfCentralDirectoryLocatorSize;

            await WriteEndOfCentralDirectoryAsync(output, cancellationToken).ConfigureAwait(false);
            written += EndOfCentralDirectorySize;

            long expected = CalculateContentLength(entries);
            if (written != expected)
            {
                throw new InvalidDataException($"ZIP archive size mismatch. Expected {expected} bytes, wrote {written} bytes.");
            }
        }

        private static ArchiveRecord CreateRecord(StoredZipArchiveEntry entry)
        {
            ValidateEntrySize(entry);
            byte[] nameBytes = GetNameBytes(entry.Name);
            (ushort Time, ushort Date) timestamp = ToDosTimestamp(entry.LastModified);
            return new ArchiveRecord(entry, nameBytes, timestamp.Time, timestamp.Date);
        }

        private static async Task<long> CopyEntryAsync(
            Stream output,
            ArchiveRecord record,
            Crc32 crc,
            CancellationToken cancellationToken)
        {
            await using Stream input = await record.Entry
                .OpenStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            byte[] buffer = new byte[128 * 1024];
            long copied = 0;

            while (true)
            {
                int read = await input
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return copied;
                }

                crc.Update(buffer.AsSpan(0, read));
                await output
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                copied += read;
            }
        }

        private static async Task WriteLocalHeaderAsync(
            Stream output,
            ArchiveRecord record,
            CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x04034b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, VersionNeededZip64, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, GeneralPurposeFlags, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, StoreMethod, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, record.DosTime, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, record.DosDate, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, (ushort)record.NameBytes.Length, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, LocalZip64ExtraSize, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(record.NameBytes, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, Zip64ExtraFieldId, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 16, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteDataDescriptorAsync(
            Stream output,
            ArchiveRecord record,
            CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x08074b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, record.Crc32, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteCentralDirectoryHeaderAsync(
            Stream output,
            ArchiveRecord record,
            CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x02014b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, VersionNeededZip64, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, VersionNeededZip64, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, GeneralPurposeFlags, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, StoreMethod, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, record.DosTime, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, record.DosDate, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, record.Crc32, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, (ushort)record.NameBytes.Length, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, CentralDirectoryZip64ExtraSize, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(record.NameBytes, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, Zip64ExtraFieldId, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 24, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.Entry.Size, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)record.LocalHeaderOffset, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteZip64EndOfCentralDirectoryAsync(
            Stream output,
            int entryCount,
            long centralDirectorySize,
            long centralDirectoryOffset,
            CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x06064b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, 44, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, VersionNeededZip64, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, VersionNeededZip64, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)entryCount, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)entryCount, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)centralDirectorySize, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)centralDirectoryOffset, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteZip64EndOfCentralDirectoryLocatorAsync(
            Stream output,
            long zip64EndOfCentralDirectoryOffset,
            CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x07064b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt64Async(output, (ulong)zip64EndOfCentralDirectoryOffset, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, 1, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteEndOfCentralDirectoryAsync(Stream output, CancellationToken cancellationToken)
        {
            await WriteUInt32Async(output, 0x06054b50, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, ushort.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, ushort.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(output, uint.MaxValue, cancellationToken).ConfigureAwait(false);
            await WriteUInt16Async(output, 0, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteUInt16Async(Stream output, ushort value, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteUInt32Async(Stream output, uint value, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteUInt64Async(Stream output, ulong value, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        private static byte[] GetNameBytes(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("ZIP entry name must not be empty.", nameof(name));
            }

            byte[] bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"ZIP entry name is too long: {name}", nameof(name));
            }

            return bytes;
        }

        private static void ValidateEntrySize(StoredZipArchiveEntry entry)
        {
            if (entry.Size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), "ZIP entry size must not be negative.");
            }
        }

        private static string NormalizeSegment(string segment)
        {
            string normalized = segment
                .Replace('\0', '_');

            if (normalized is "" or ".")
            {
                return string.Empty;
            }

            if (normalized == "..")
            {
                return "__";
            }

            if (normalized.Length == 2 && normalized[1] == ':')
            {
                return normalized[0] + "_";
            }

            return normalized;
        }

        private static (ushort Time, ushort Date) ToDosTimestamp(DateTime? lastModified)
        {
            DateTime value = lastModified ?? new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            value = value.Kind == DateTimeKind.Unspecified ? value : value.ToUniversalTime();

            if (value.Year < 1980)
            {
                value = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
            else if (value.Year > 2107)
            {
                value = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
            }

            ushort time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
            ushort date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
            return (time, date);
        }

        private class ArchiveRecord(
            StoredZipArchiveEntry entry,
            byte[] nameBytes,
            ushort dosTime,
            ushort dosDate)
        {
            public StoredZipArchiveEntry Entry { get; } = entry;
            public byte[] NameBytes { get; } = nameBytes;
            public ushort DosTime { get; } = dosTime;
            public ushort DosDate { get; } = dosDate;
            public long LocalHeaderOffset { get; set; }
            public uint Crc32 { get; set; }
        }

        private class Crc32
        {
            private static readonly uint[] Table = CreateTable();
            private uint _value = uint.MaxValue;

            public uint Value => ~_value;

            public void Update(ReadOnlySpan<byte> data)
            {
                foreach (byte item in data)
                {
                    _value = Table[(_value ^ item) & 0xff] ^ (_value >> 8);
                }
            }

            private static uint[] CreateTable()
            {
                uint[] table = new uint[256];
                for (uint i = 0; i < table.Length; i++)
                {
                    uint value = i;
                    for (int j = 0; j < 8; j++)
                    {
                        value = (value & 1) == 1
                            ? 0xedb88320 ^ (value >> 1)
                            : value >> 1;
                    }

                    table[i] = value;
                }

                return table;
            }
        }
    }
}
