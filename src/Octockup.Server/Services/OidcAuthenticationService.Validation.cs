// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Octockup.Server.Database;
using Octockup.Server.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Octockup.Server.Services
{
    public partial class OidcAuthenticationService
    {
        private ClaimsPrincipal ValidateIdToken(
            OpenIdConnectConfiguration configuration,
            OidcProvider provider,
            string idToken,
            string nonce)
        {
            try
            {
                JwtSecurityTokenHandler handler = new()
                {
                    MapInboundClaims = false,
                };
                TokenValidationParameters validationParameters = CreateTokenValidationParameters(
                    configuration,
                    provider);
                ClaimsPrincipal principal = handler.ValidateToken(
                    idToken,
                    validationParameters,
                    out SecurityToken validatedToken);
                JwtSecurityToken jwt = GetSignedJwt(validatedToken);
                ValidateNonce(principal, nonce);
                ValidateAuthorizedParty(principal, jwt, provider.ClientId);
                return principal;
            }
            catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
            {
                _logger.LogWarning(
                    exception,
                    "OIDC ID token validation failed for provider {ProviderId}",
                    provider.Id);
                throw new AuthApiException(StatusCodes.Status400BadRequest, "OIDC ID token is invalid.");
            }
        }

        private static TokenValidationParameters CreateTokenValidationParameters(
            OpenIdConnectConfiguration configuration,
            OidcProvider provider)
        {
            TokenValidationParameters parameters = new()
            {
                ValidateIssuer = true,
                ValidIssuer = configuration.Issuer ?? provider.Issuer,
                ValidateAudience = true,
                ValidAudience = provider.ClientId,
                ValidateLifetime = true,
                ClockSkew = ClockSkew,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                NameClaimType = "name",
            };
            if (configuration.IdTokenSigningAlgValuesSupported.Count > 0)
            {
                parameters.ValidAlgorithms = configuration.IdTokenSigningAlgValuesSupported;
            }

            return parameters;
        }

        private static JwtSecurityToken GetSignedJwt(SecurityToken validatedToken)
        {
            if (validatedToken is JwtSecurityToken jwt
                && !string.Equals(
                    jwt.Header.Alg,
                    SecurityAlgorithms.None,
                    StringComparison.OrdinalIgnoreCase))
            {
                return jwt;
            }

            throw new SecurityTokenValidationException("Unsigned ID token.");
        }

        private static void ValidateNonce(ClaimsPrincipal principal, string nonce)
        {
            string? tokenNonce = FindClaim(principal, "nonce");
            if (!string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
            {
                throw new SecurityTokenValidationException("Invalid nonce.");
            }
        }

        private static void ValidateAuthorizedParty(
            ClaimsPrincipal principal,
            JwtSecurityToken jwt,
            string clientId)
        {
            List<string> audiences = jwt.Audiences.ToList();
            string? authorizedParty = FindClaim(principal, "azp");
            if ((audiences.Count > 1 || authorizedParty is not null)
                && !string.Equals(authorizedParty, clientId, StringComparison.Ordinal))
            {
                throw new SecurityTokenValidationException("Invalid authorized party.");
            }
        }

        private static OidcIdentityClaims ReadIdentityClaims(ClaimsPrincipal principal)
        {
            string subject = RequiredClaim(principal, JwtRegisteredClaimNames.Sub, 256);
            string? email = OptionalClaim(principal, 320, JwtRegisteredClaimNames.Email, "email");
            string? displayName = OptionalClaim(principal, 160, "name", "preferred_username");
            return new OidcIdentityClaims(subject, email, displayName, ReadAvatarUrl(principal));
        }

        private static string? ReadAvatarUrl(ClaimsPrincipal principal)
        {
            string? picture = FindClaim(principal, "picture")?.Trim();
            if (picture is null || picture.Length > UserExternalIdentity.MaxAvatarUrlLength
                || !Uri.TryCreate(picture, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
                || !uri.IsWellFormedOriginalString())
            {
                return null;
            }

            return picture;
        }

        private static string RequiredClaim(ClaimsPrincipal principal, string type, int maxLength)
        {
            string? value = FindClaim(principal, type)?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            {
                throw new AuthApiException(
                    StatusCodes.Status400BadRequest,
                    $"OIDC {type} claim is missing or invalid.");
            }

            return value;
        }

        private static string? OptionalClaim(
            ClaimsPrincipal principal,
            int maxLength,
            params string[] types)
        {
            foreach (string type in types)
            {
                string? value = FindClaim(principal, type)?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                if (value.Length > maxLength)
                {
                    throw new AuthApiException(
                        StatusCodes.Status400BadRequest,
                        $"OIDC {type} claim is too long.");
                }

                return value;
            }

            return null;
        }

        private static string? FindClaim(ClaimsPrincipal principal, string type)
        {
            return principal.FindFirst(type)?.Value;
        }

        private static void ApplyClaims(UserExternalIdentity identity, OidcIdentityClaims claims)
        {
            identity.Email = claims.Email;
            identity.DisplayName = claims.DisplayName;
            identity.AvatarUrl = claims.AvatarUrl;
            identity.LastUsedAt = DateTime.UtcNow;
        }
    }
}
