// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using EasyExtensions.Mediator;
using EasyExtensions.Mediator.Contracts;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Database;
using Octockup.Server.Models.Dto;

namespace Octockup.Server.Handlers.Authentication
{
    public record GetCurrentUserQuery(Guid UserId) : IRequest<CurrentUserDto?>;

    public class GetCurrentUserQueryHandler(AppDbContext dbContext)
        : IRequestHandler<GetCurrentUserQuery, CurrentUserDto?>
    {
        public Task<CurrentUserDto?> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
        {
            return dbContext.Users.AsNoTracking()
                .Where(user => user.Id == request.UserId)
                .Select(user => new CurrentUserDto
                {
                    Id = user.Id,
                    CreatedAt = user.CreatedAt,
                    UpdatedAt = user.UpdatedAt,
                    Username = user.Username,
                    DisplayName = user.Username + "@octockup",
                    IsAdmin = user.IsAdmin,
                    IsDisabled = user.IsDisabled,
                    ExternalIdentityCount = user.ExternalIdentities.Count,
                    AvatarUrl = user.ExternalIdentities
                        .Where(x => x.AvatarUrl != null && x.LastUsedAt != null && x.Provider.IsEnabled && x.Issuer == x.Provider.Issuer)
                        .OrderByDescending(x => x.LastUsedAt)
                        .ThenBy(x => x.Id)
                        .Select(x => x.AvatarUrl)
                        .FirstOrDefault(),
                })
                .SingleOrDefaultAsync(cancellationToken);
        }
    }
}
