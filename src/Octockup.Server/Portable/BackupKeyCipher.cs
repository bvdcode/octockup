// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Abstractions;
using EasyExtensions.Crypto;
using EasyExtensions.Extensions;
using Octockup.Server.Database;
namespace Octockup.Server.Portable
{
    public static class BackupKeyCipher
    {
        public static IStreamCipher Resolve(Backup backup, IStreamCipher applicationCipher)
        {
            if (backup.EncryptedMasterKey is null)
            {
                return applicationCipher;
            }
            string master = applicationCipher.DecryptString(Convert.FromBase64String(backup.EncryptedMasterKey));
            return new AesGcmStreamCipher(KeyDerivation.DeriveSubkey(master, "crypto", 32));
        }
    }
}
