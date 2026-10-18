// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator.Contracts;

namespace Octockup.Server.Handlers.Deletion
{
    public record ProcessPendingDeletionsCommand : IRequest<int>;
}
