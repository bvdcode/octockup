// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using Octockup.Server.Abstractions;
namespace Octockup.Server.Portable
{
    public record PortableAccess(IBackupStorage Storage, Guid ModuleId, IStreamCipher Cipher);
}
