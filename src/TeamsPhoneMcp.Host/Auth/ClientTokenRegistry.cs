using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Host.Auth;

/// <summary>
/// Validates the configured bearer tokens and rejects sessions that change hands.
/// </summary>
internal sealed class BearerAuthOptionsValidator : IValidateOptions<BearerAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, BearerAuthOptions options)
    {
        var failures = new List<string>();
        var resolved = options.ResolveClientTokens();

        var duplicateClients = resolved
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var clientId in duplicateClients)
        {
            failures.Add(
                $"Auth: client id '{clientId}' is configured more than once. " +
                $"Auth:BearerToken is the '{BearerAuthOptions.DefaultClientId}' client; do not also " +
                $"set Auth:ClientTokens:{BearerAuthOptions.DefaultClientId}.");
        }

        // Sharing one token across client ids would make audit attribution ambiguous, which
        // the build spec (§9.1) requires to be unfalsifiable.
        var sharedTokenClients = resolved
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        foreach (var clients in sharedTokenClients)
        {
            failures.Add(
                $"Auth: client ids {string.Join(", ", clients.Select(id => $"'{id}'"))} share the same " +
                "token, so audit records could not attribute them distinctly. Give each client its own token.");
        }

        foreach (var (clientId, token) in resolved)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                failures.Add("Auth: client ids under Auth:ClientTokens must not be blank.");
            }

            if (token.Length > BearerAuthMiddleware.MaxTokenLength)
            {
                failures.Add(
                    $"Auth: the token for client '{clientId}' exceeds the maximum allowed length " +
                    $"of {BearerAuthMiddleware.MaxTokenLength} characters.");
            }
        }

        // A policy naming a client that does not exist is almost always a typo, and silently
        // ignoring it would leave the caller the operator meant to restrict fully able to write.
        var configuredClients = resolved
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var clientId in options.ClientPolicy.Keys)
        {
            if (!configuredClients.Contains(clientId))
            {
                failures.Add(
                    $"Auth: Auth:ClientPolicy:{clientId} does not match any configured client. " +
                    "Add a token for it under Auth:ClientTokens, or remove the policy entry.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Result of matching a presented bearer token against the configured clients.
/// </summary>
internal readonly record struct ClientAuthentication(bool IsAuthenticated, string? ClientId, bool WhatIfMode)
{
    public static ClientAuthentication Anonymous => default;

    public static ClientAuthentication For(string clientId, bool whatIfMode) =>
        new(true, clientId, whatIfMode);
}

/// <summary>
/// Resolves a presented bearer token to the client id it was issued to, in constant time
/// with respect to which token matched.
/// </summary>
internal sealed class ClientTokenRegistry
{
    private readonly IReadOnlyList<(string ClientId, byte[] TokenHash, bool WhatIfMode)> _clients;

    public ClientTokenRegistry(BearerAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Hashing both sides to a fixed 32 bytes keeps FixedTimeEquals from exiting early on
        // a length mismatch, which would otherwise leak the configured token's length.
        _clients = options.ResolveClientTokens()
            .Select(pair => (
                ClientId: pair.Key,
                TokenHash: SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value)),
                options.ResolvePolicy(pair.Key).WhatIfMode))
            .ToList();
    }

    public bool IsEmpty => _clients.Count == 0;

    public ClientAuthentication Authenticate(ReadOnlySpan<byte> presented)
    {
        var presentedHash = SHA256.HashData(presented);

        // Every entry is compared even after a match so the work done does not reveal
        // which client matched, or how many entries precede it.
        string? matched = null;
        var matchedWhatIfMode = false;
        foreach (var (clientId, tokenHash, whatIfMode) in _clients)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, tokenHash))
            {
                if (matched is null)
                {
                    matched = clientId;
                    matchedWhatIfMode = whatIfMode;
                }
            }
        }

        return matched is null
            ? ClientAuthentication.Anonymous
            : ClientAuthentication.For(matched, matchedWhatIfMode);
    }
}
