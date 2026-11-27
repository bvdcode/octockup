// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using EasyExtensions;
using EasyExtensions.Abstractions;
using EasyExtensions.AspNetCore.Authorization.Models.Dto;
using EasyExtensions.AspNetCore.Extensions;
using EasyExtensions.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octockup.Server.Abstractions;
using Octockup.Server.Database;
using Octockup.Server.Handlers.Authentication;
using Octockup.Server.Models.Dto;
using Octockup.Server.Models.Requests;
using Octockup.Server.Services;

namespace Octockup.Server.Controllers
{
    [ApiController]
    [Route("/api/v1/auth")]
    public class AuthController(
        AppDbContext _dbContext,
        ILogger<ActionContext> _logger,
        IPasswordHashService _passwords,
        AuthenticationSettingsService _authenticationSettings,
        IAuthSessionIssuer _sessionIssuer) : ControllerBase
    {
        [Authorize]
        [HttpGet("me")]
        public async Task<ActionResult<CurrentUserDto>> MeAsync(
            [FromServices] IMediator mediator,
            CancellationToken cancellationToken)
        {
            CurrentUserDto? user = await mediator.Send(new GetCurrentUserQuery(User.GetUserId()), cancellationToken);
            if (user is null)
            {
                return NotFound();
            }
            return Ok(user);
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePasswordAsync(
            [FromBody] ChangePasswordRequest request,
            CancellationToken cancellationToken)
        {
            Guid userId = User.GetUserId();
            User? user = await _dbContext.Users.FindAsync([userId], cancellationToken);
            if (user == null)
            {
                return NotFound();
            }
            bool isValid = _passwords.Verify(request.OldPassword, user.PasswordPhc);
            if (!isValid)
            {
                return this.ApiBadRequest("Old password is incorrect.");
            }
            user.PasswordPhc = _passwords.Hash(request.NewPassword);
            _dbContext.Users.Update(user);
            int result = await _dbContext.SaveChangesAsync(cancellationToken);
            bool success = result > 0;
            if (!success)
            {
                return this.ApiBadRequest("Password change failed.");
            }
            return Ok("Password changed successfully.");
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> RefreshTokenAsync(
            [FromBody] RefreshTokenRequestDto request,
            CancellationToken cancellationToken)
        {
            string? refreshToken = request.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                if (Request.Cookies.TryGetValue("refresh_token", out string? cookieToken))
                {
                    refreshToken = cookieToken;
                }
            }
            if (string.IsNullOrEmpty(refreshToken))
            {
                return this.ApiUnauthorized("Invalid refresh token.");
            }

            TokenPairResponseDto? tokens = await _sessionIssuer.RotateAsync(
                refreshToken,
                Response,
                cancellationToken);
            if (tokens is null)
            {
                return this.ApiUnauthorized("Invalid refresh token.");
            }

            return Ok(tokens);
        }

        [HttpPost("login")]
        public async Task<IActionResult> LoginAsync(
            [FromBody] LoginRequestDto request,
            CancellationToken cancellationToken)
        {
            User? user = await AuthMutationTransaction.ExecuteAsync(
                _dbContext,
                () => AuthenticatePasswordAsync(request, cancellationToken),
                cancellationToken);
            if (user is null)
            {
                return this.ApiUnauthorized("Invalid username or password.");
            }

            TokenPairResponseDto tokens = await _sessionIssuer.IssueAsync(
                user,
                Response,
                cancellationToken);
            _logger.LogInformation("User '{user}' logged in", user.Username);
            return Ok(tokens);
        }

        private async Task<User?> AuthenticatePasswordAsync(
            LoginRequestDto request,
            CancellationToken cancellationToken)
        {
            if (!await _authenticationSettings.IsPasswordLoginEnabledAsync(cancellationToken))
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return null;
            }

            string requestedUsername = request.Username ?? string.Empty;
            User? user = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Username == requestedUsername, cancellationToken);
            if (user == null)
            {
                int userCount = await _dbContext.Users.CountAsync(cancellationToken);
                if (userCount > 0)
                {
                    return null;
                }
                if (!UsernameValidator.TryNormalize(requestedUsername, out string normalizedUsername))
                {
                    return null;
                }
                user = new()
                {
                    Username = normalizedUsername,
                    PasswordPhc = _passwords.Hash(request.Password),
                    IsAdmin = userCount == 0,
                    IsDisabled = false,
                };
                _dbContext.Users.Add(user);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            if (user.IsDisabled)
            {
                return null;
            }
            bool isValid = _passwords.Verify(request.Password, user.PasswordPhc, out bool needsRehash);
            if (!isValid)
            {
                _logger.LogWarning("Invalid login attempt for user '{user}'", user.Username);
                return null;
            }

            if (needsRehash)
            {
                user.PasswordPhc = _passwords.Hash(request.Password);
                _dbContext.Users.Update(user);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return user;
        }

        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
        {
            string refreshToken = Request.Cookies["refresh_token"] ?? string.Empty;
            EasyExtensions.EntityFrameworkCore.Database.RefreshToken? foundToken = await _dbContext.RefreshTokens
                .FirstOrDefaultAsync(
                    x => x.Token == refreshToken && x.RevokedAt == null,
                    cancellationToken);
            if (foundToken != null)
            {
                foundToken.RevokedAt = DateTime.UtcNow;
                _dbContext.RefreshTokens.Update(foundToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            Response.Cookies.Delete(
                "refresh_token",
                new CookieOptions { Path = "/api/v1/auth" });
            return Ok("Logged out successfully.");
        }
    }
}
