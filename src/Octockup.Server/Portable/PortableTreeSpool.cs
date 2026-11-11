// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Runtime.CompilerServices;
using System.Text.Json;
namespace Octockup.Server.Portable
{
    public class PortableTreeSpool : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("octockup-catalog-").FullName;
        private StreamWriter? _writer;
        private string? _currentDirectory;
        public async Task AddAsync(PortableEntry entry, CancellationToken ct)
        {
            string directory = Parent(entry.Path);
            await EnsureDirectoryAsync(directory, ct);
            if (_currentDirectory != directory)
            {
                await CloseWriterAsync();
                _writer = new StreamWriter(new FileStream(EntriesPath(directory), FileMode.Append, FileAccess.Write));
                _currentDirectory = directory;
            }
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(entry).AsMemory(), ct);
        }
        public async Task EnsureDirectoryAsync(string path, CancellationToken ct)
        {
            string directory = LocalDirectory(path);
            if (Directory.Exists(directory))
            {
                return;
            }
            await CloseWriterAsync();
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "path"), path, ct);
            await File.WriteAllTextAsync(EntriesPath(path), string.Empty, ct);
            if (path.Length > 0)
            {
                await AddAsync(new(path, path[(path.LastIndexOf('/') + 1)..], true, 0, null, null, []), ct);
            }
        }
        public async IAsyncEnumerable<(string Path, List<PortableEntry> Entries, int Page)> ReadPagesAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await CloseWriterAsync();
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                string path = await File.ReadAllTextAsync(Path.Combine(directory, "path"), ct);
                await using FileStream stream = File.OpenRead(EntriesPath(path));
                List<PortableEntry> entries = [];
                int page = 0;
                await foreach (PortableEntry? entry in JsonSerializer.DeserializeAsyncEnumerable<PortableEntry>(
                    stream, topLevelValues: true, cancellationToken: ct))
                {
                    if (entry is null)
                    {
                        throw new InvalidDataException("Invalid catalog entry.");
                    }
                    entries.Add(entry);
                    if (entries.Count == PortableFormat.PageSize)
                    {
                        yield return (path, entries, page++);
                        entries = [];
                    }
                }
                if (entries.Count > 0 || page == 0)
                {
                    yield return (path, entries, page);
                }
            }
        }
        private string LocalDirectory(string path) => Path.Combine(_root, PortableFormat.DirectoryId(path));
        private string EntriesPath(string path) => Path.Combine(LocalDirectory(path), "entries");
        private static string Parent(string path)
        {
            int index = path.LastIndexOf('/');
            return index < 0 ? string.Empty : path[..index];
        }
        private async ValueTask CloseWriterAsync()
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync();
                _writer = null;
                _currentDirectory = null;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await CloseWriterAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
