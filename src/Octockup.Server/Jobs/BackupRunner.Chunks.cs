// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions.Models.Enums;
using EasyExtensions.Streams;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Helpers;
using Octockup.Server.Models;
using System.Buffers;
using System.Security.Cryptography;

namespace Octockup.Server.Jobs
{
    public partial class BackupRunner
    {
        private async Task<(string FileHash, List<string> ChunkHashes)> ProcessChunksAsync(
            Schedule schedule,
            BackupFileInfo file,
            IBackupStorage storage,
            ScheduleReport report,
            HashSet<ChunkKeyIdentity> uploadedChunks,
            Stream stream,
            int counter,
            CancellationToken cancellationToken)
        {
            using ChunkedStream chunker = new(stream, ChunkSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                using IncrementalHash fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                List<string> chunkHashes = [];

                foreach (Stream chunk in chunker.GetChunks())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    chunk.Seek(0, SeekOrigin.Begin);
                    using IncrementalHash chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int read;
                    long chunkLength = 0L;
                    while ((read = await chunk.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, ChunkSize)),
                        cancellationToken)) > 0)
                    {
                        chunkHasher.AppendData(buffer, 0, read);
                        fileHasher.AppendData(buffer, 0, read);
                        chunkLength += read;
                    }

                    string contentHash = Convert.ToHexString(chunkHasher.GetHashAndReset()).ToLowerInvariant();
                    CompressionAlgorithm algorithm = schedule.Backup.DisableCompression
                        ? CompressionAlgorithm.None
                        : CompressionHelpers.Algorithm;
                    bool encryptChunk = !schedule.Backup.DisableEncryption;
                    string chunkKey = ChunkStorageHelpers.CreateKey(contentHash, algorithm, encryptChunk);
                    if (_keyScope is not null)
                    {
                        chunkKey = $"v3-{_keyScope}-{chunkKey}";
                    }
                    ChunkKeyIdentity chunkIdentity = ChunkKeyIdentity.Parse(chunkKey);
                    string shortHash = contentHash[^8..];

                    if (uploadedChunks.Contains(chunkIdentity))
                    {
                        logger.LogInformation(
                            "Chunk {shortHash} for file {FileName} already uploaded in previous snapshot, skipping upload",
                            shortHash,
                            file.Name);
                        chunkHashes.Add(chunkKey);
                        await report.SendAsync(
                            counter,
                            $"Processing: {file.Name}",
                            processedBytes: chunkLength,
                            cancellationToken: cancellationToken);
                        await chunk.DisposeAsync();
                        continue;
                    }

                    logger.LogInformation(
                        "Processing chunk {shortHash} for file {FileName}",
                        shortHash,
                        file.Path);
                    await UploadChunkAsync(
                        schedule,
                        file,
                        storage,
                        report,
                        uploadedChunks,
                        chunk,
                        buffer,
                        chunkLength,
                        contentHash,
                        chunkKey,
                        chunkIdentity,
                        shortHash,
                        algorithm,
                        encryptChunk,
                        chunkHashes,
                        counter,
                        cancellationToken);
                }

                string fileHash = Convert.ToHexString(fileHasher.GetHashAndReset()).ToLowerInvariant();
                return (fileHash, chunkHashes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task UploadChunkAsync(
            Schedule schedule,
            BackupFileInfo file,
            IBackupStorage storage,
            ScheduleReport report,
            HashSet<ChunkKeyIdentity> uploadedChunks,
            Stream chunk,
            byte[] buffer,
            long chunkLength,
            string contentHash,
            string chunkKey,
            ChunkKeyIdentity chunkIdentity,
            string shortHash,
            CompressionAlgorithm algorithm,
            bool encryptChunk,
            List<string> chunkHashes,
            int counter,
            CancellationToken cancellationToken)
        {
            string path = ChunkStorageHelpers.GetStoragePath(chunkKey, storage.PathSeparator);
            string size = $"{(chunkLength / (1024.0 * 1024.0)):F2} MB";
            logger.LogInformation(
                "Uploading chunk {shortHash} for file {FileName}, size: {size}",
                shortHash,
                file.Name,
                size);
            chunk.Seek(0, SeekOrigin.Begin);

            MemoryStream? compressedStream = null;
            Stream source = chunk;
            if (algorithm != CompressionAlgorithm.None)
            {
                compressedStream = new MemoryStream();
                await using (Stream compressed = CompressionHelpers.CreateCompressionStream(compressedStream))
                {
                    int read;
                    while ((read = await chunk.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, ChunkSize)),
                        cancellationToken)) > 0)
                    {
                        await compressed.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                compressedStream.Seek(0, SeekOrigin.Begin);
                source = compressedStream;
                logger.LogInformation(
                    "Chunk {shortHash} for file {FileName} compressed from {originalSize} to {compressedSize} using {algorithm}",
                    shortHash,
                    file.Name,
                    size,
                    $"{(source.Length / (1024.0 * 1024.0)):F2} MB",
                    algorithm);
            }
            else
            {
                logger.LogInformation(
                    "Chunk {shortHash} for file {FileName} stored without compression",
                    shortHash,
                    file.Name);
            }

            try
            {
                long storedSize = await UploadChunkDataAsync(
                    storage,
                    path,
                    source,
                    chunkLength,
                    encryptChunk,
                    cancellationToken);
                uploadedChunks.Add(chunkIdentity);
                await EnsureUploadedHashRecordedAsync(
                    schedule.Backup.StorageId,
                    chunkKey,
                    storedSize,
                    chunkLength,
                    algorithm,
                    cancellationToken);
                await report.SendAsync(
                    counter,
                    $"Uploading: {file.Name}",
                    processedBytes: chunkLength,
                    cancellationToken: cancellationToken);
                chunkHashes.Add(chunkKey);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (compressedStream is not null)
                {
                    await compressedStream.DisposeAsync();
                }
            }

            await chunk.DisposeAsync();
            if (_pendingUploadedHashes.Count >= UploadedHashesFlushCount
                || _uploadedHashesStopwatch.Elapsed > UploadedHashesFlushInterval)
            {
                await FlushUploadedHashesAsync(cancellationToken);
            }
        }

        private async Task<long> UploadChunkDataAsync(
            IBackupStorage storage,
            string path,
            Stream source,
            long chunkLength,
            bool encryptChunk,
            CancellationToken cancellationToken)
        {
            if (source.CanSeek)
            {
                source.Seek(0, SeekOrigin.Begin);
            }

            if (!encryptChunk)
            {
                long storedSize = source.CanSeek ? source.Length : chunkLength;
                await storage.UploadAsync(path, source, cancellationToken);
                return storedSize;
            }

            using MemoryStream encryptedStream = new();
            await _chunkCipher.EncryptAsync(source, encryptedStream, ct: cancellationToken);
            encryptedStream.Seek(0, SeekOrigin.Begin);
            long encryptedSize = encryptedStream.Length;
            await storage.UploadAsync(path, encryptedStream, cancellationToken);
            return encryptedSize;
        }

        private async Task EnsureUploadedHashRecordedAsync(
            Guid storageModuleId,
            string chunkKey,
            long storedSize,
            long originalSize,
            CompressionAlgorithm algorithm,
            CancellationToken cancellationToken)
        {
            bool chunkRecorded = await dbContext.UploadedHashes
                .AsNoTracking()
                .AnyAsync(x => x.Hash == chunkKey && x.ModuleId == storageModuleId, cancellationToken);
            if (chunkRecorded)
            {
                return;
            }

            UploadedHash uploadedHash = new()
            {
                Hash = chunkKey,
                StoredSize = storedSize,
                OriginalSize = originalSize,
                ModuleId = storageModuleId,
                CompressionAlgorithm = algorithm,
            };
            _pendingUploadedHashes.Add(uploadedHash);
        }

        private async Task FlushUploadedHashesAsync(CancellationToken cancellationToken)
        {
            if (_pendingUploadedHashes.Count == 0)
            {
                _uploadedHashesStopwatch.Restart();
                return;
            }

            await dbContext.UploadedHashes.AddRangeAsync(_pendingUploadedHashes, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            _pendingUploadedHashes.Clear();
            _uploadedHashesStopwatch.Restart();
        }
    }
}
