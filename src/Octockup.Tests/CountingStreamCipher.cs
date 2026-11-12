// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;

namespace Octockup.Tests
{
    internal class CountingStreamCipher(IStreamCipher inner) : IStreamCipher
    {
        public int Decryptions { get; private set; }
        public Task EncryptAsync(Stream input, Stream output, int chunkSize, bool leaveInputOpen,
            bool leaveOutputOpen, CancellationToken ct) =>
            inner.EncryptAsync(input, output, chunkSize, leaveInputOpen, leaveOutputOpen, ct);
        public Task<Stream> EncryptAsync(Stream input, int chunkSize, bool leaveOpen, CancellationToken ct) =>
            inner.EncryptAsync(input, chunkSize, leaveOpen, ct);
        public Task DecryptAsync(Stream input, Stream output, bool leaveInputOpen, bool leaveOutputOpen, CancellationToken ct)
        {
            Decryptions++;
            return inner.DecryptAsync(input, output, leaveInputOpen, leaveOutputOpen, ct);
        }
        public Task<Stream> DecryptAsync(Stream input, bool leaveOpen, CancellationToken ct)
        {
            Decryptions++;
            return inner.DecryptAsync(input, leaveOpen, ct);
        }
    }
}
