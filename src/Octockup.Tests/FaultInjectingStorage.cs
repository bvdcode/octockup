// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Abstractions;
using Octockup.Server.Models;

namespace Octockup.Tests
{
    internal class FaultInjectingStorage(IBackupStorage inner) : IBackupStorage
    {
        public Func<string, bool>? FailUpload { get; set; }
        public Func<string, bool>? FailDelete { get; set; }
        public List<string> Uploads { get; } = [];
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);
        public string Id => inner.Id;
        public string Name => inner.Name;
        public char PathSeparator => inner.PathSeparator;
        public IEnumerable<string> RequiredParameters => inner.RequiredParameters;
        public void SetParameters(IReadOnlyDictionary<string, string> parameters) => inner.SetParameters(parameters);
        public void SetIgnoredPaths(ICollection<string>? ignoredPaths) => inner.SetIgnoredPaths(ignoredPaths);
        public Task<BackupFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken) =>
            inner.GetFileInfoAsync(path, cancellationToken);
        public Task<Stream> GetFileStreamAsync(BackupFileInfo file, CancellationToken cancellationToken = default) =>
            inner.GetFileStreamAsync(file, cancellationToken);
        public IEnumerable<string> GetDirectories(bool recursive = false, CancellationToken cancellationToken = default) =>
            inner.GetDirectories(recursive, cancellationToken);
        public IEnumerable<BackupFileInfo> GetFiles(bool recursive = false, CancellationToken cancellationToken = default) =>
            inner.GetFiles(recursive, cancellationToken);
        public Task<bool?> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(path, cancellationToken);
        public async Task<bool?> DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            if (FailDelete?.Invoke(path) == true)
            {
                throw new IOException("Storage rejected deletion.");
            }
            bool? deleted = await inner.DeleteAsync(path, cancellationToken);
            Objects.Remove(path);
            return deleted;
        }
        public async Task UploadAsync(string path, Stream data, CancellationToken cancellationToken = default)
        {
            Uploads.Add(path);
            if (FailUpload?.Invoke(path) == true)
            {
                throw new IOException("Storage rejected upload.");
            }
            await inner.UploadAsync(path, data, cancellationToken);
            Objects.Add(path);
        }
    }
}
