// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Octockup.Server.Abstractions;
using Octockup.Server.Models.Enums;

namespace Octockup.Server.Handlers.Modules
{
    public record PreparedModuleConnection(IBackupSource Provider, ModuleDestination Destination);
}
