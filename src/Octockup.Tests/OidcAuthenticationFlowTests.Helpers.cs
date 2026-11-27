// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Octockup.Server.Database;
using Octockup.Server.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Octockup.Tests
{
    public partial class OidcAuthenticationFlowTests
    {
        private OidcAuthenticationService CreateService(
            PostgresDbContext dbContext,
            HttpClient httpClient,
            RecordingAuthSessionIssuer sessionIssuer)
        {
            OidcDiscoveryService discovery = new(
                httpClient,
                NullLogger<OidcDiscoveryService>.Instance);
            OidcProviderService providers = new(dbContext, _cipher);
            AuthenticationSettingsService settings = new(dbContext);
            return new OidcAuthenticationService(
                dbContext,
                discovery,
                providers,
                settings,
                sessionIssuer,
                _cipher,
                NullLogger<OidcAuthenticationService>.Instance);
        }

        private async Task<OidcProvider> CreateProviderAsync(PostgresDbContext dbContext)
        {
            string suffix = Guid.NewGuid().ToString("N");
            OidcProvider provider = new()
            {
                Name = "Provider " + suffix,
                Slug = "provider-" + suffix,
                Issuer = "https://issuer-" + suffix + ".example",
                PublicBaseUrl = "https://octockup.example",
                ClientId = "client-" + suffix,
                Scopes = ["openid", "profile", "email"],
                IsEnabled = true,
            };
            await dbContext.OidcProviders.AddAsync(provider);
            await dbContext.SaveChangesAsync();
            return provider;
        }

        private string CreateIdToken(OidcProvider provider, string nonce, string subject, string? picture = null)
        {
            RsaSecurityKey securityKey = new(_rsa)
            {
                KeyId = "test-key",
            };
            SigningCredentials credentials = new(securityKey, SecurityAlgorithms.RsaSha256);
            List<Claim> claims =
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim("nonce", nonce),
                new Claim(JwtRegisteredClaimNames.Email, "user@example.com"),
                new Claim("name", "Test User"),
            ];
            if (picture is not null)
            {
                claims.Add(new Claim("picture", picture));
            }
            JwtSecurityToken token = new(
                issuer: provider.Issuer + "/",
                audience: provider.ClientId,
                claims: claims,
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private PostgresDbContext CreateDbContext(SaveChangesBarrierInterceptor? interceptor = null)
        {
            DbContextOptionsBuilder<PostgresDbContext> builder = new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(_database.ConnectionString);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return new PostgresDbContext(builder.Options);
        }

        private static async Task<Exception?> CaptureExceptionAsync(Func<Task> operation)
        {
            try
            {
                await operation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static DefaultHttpContext CreateCallbackContext(
            DefaultHttpContext beginContext,
            bool useValidValue)
        {
            string setCookie = beginContext.Response.Headers.SetCookie.ToString();
            string cookiePair = setCookie.Split(';', 2)[0];
            string cookieName = cookiePair.Split('=', 2)[0];
            DefaultHttpContext callbackContext = new();
            callbackContext.Request.Headers.Cookie = useValidValue
                ? cookiePair
                : cookieName + "=wrong-state";
            return callbackContext;
        }
    }
}
