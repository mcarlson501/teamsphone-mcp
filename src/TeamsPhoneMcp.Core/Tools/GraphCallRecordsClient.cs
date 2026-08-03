using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using TeamsPhoneMcp.Credentials;

namespace TeamsPhoneMcp.Core.Tools;

public interface IGraphCallRecordsClient
{
    Task<GraphPstnCallData> GetPstnCallsAsync(
        GraphTenantContext context,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<GraphQualityCallData> GetQualityCallsAsync(
        GraphTenantContext context,
        string userPrincipalName,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}

public sealed record GraphTenantContext(Guid TenantId, string CredentialRef);

public sealed record GraphPstnCallData(
    IReadOnlyList<JsonElement> PstnCalls,
    IReadOnlyList<JsonElement> DirectRoutingCalls,
    bool Truncated);

public sealed record GraphQualityCallData(
    string UserId,
    IReadOnlyList<JsonElement> Calls,
    bool Truncated);

public sealed class GraphCallRecordsException(string errorCode, string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

internal sealed class UnconfiguredGraphCallRecordsClient : IGraphCallRecordsClient
{
    public Task<GraphPstnCallData> GetPstnCallsAsync(
        GraphTenantContext context,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        throw new GraphCallRecordsException(
            "graphClientUnavailable",
            "Microsoft Graph call records are not configured for this server.");

    public Task<GraphQualityCallData> GetQualityCallsAsync(
        GraphTenantContext context,
        string userPrincipalName,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        throw new GraphCallRecordsException(
            "graphClientUnavailable",
            "Microsoft Graph call records are not configured for this server.");
}

public sealed class GraphCallRecordsClient(ICredentialProvider credentialProvider) : IGraphCallRecordsClient
{
    private const int MaxRowsPerCollection = 10_000;
    private const int MaxQualityRecords = 100;
    private const int MaxSessionsPerCall = 1_000;

    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/", UriKind.Absolute),
        Timeout = TimeSpan.FromMinutes(2),
    };

    private static readonly TokenRequestContext TokenRequest =
        new(["https://graph.microsoft.com/.default"]);

    public async Task<GraphPstnCallData> GetPstnCallsAsync(
        GraphTenantContext context,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(context, cancellationToken).ConfigureAwait(false);
        var from = Uri.EscapeDataString(FormatGraphDate(fromUtc));
        var to = Uri.EscapeDataString(FormatGraphDate(toUtc));
        var pstn = await GetCollectionAsync(
            $"communications/callRecords/getPstnCalls(fromDateTime={from},toDateTime={to})",
            token,
            MaxRowsPerCollection,
            cancellationToken).ConfigureAwait(false);
        var directRouting = await GetCollectionAsync(
            $"communications/callRecords/getDirectRoutingCalls(fromDateTime={from},toDateTime={to})",
            token,
            MaxRowsPerCollection,
            cancellationToken).ConfigureAwait(false);

        return new GraphPstnCallData(
            pstn.Items,
            directRouting.Items,
            pstn.Truncated || directRouting.Truncated);
    }

    public async Task<GraphQualityCallData> GetQualityCallsAsync(
        GraphTenantContext context,
        string userPrincipalName,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(context, cancellationToken).ConfigureAwait(false);
        var userPath = $"users/{Uri.EscapeDataString(userPrincipalName)}?$select=id,userPrincipalName";
        using var userDocument = await GetDocumentAsync(userPath, token, cancellationToken).ConfigureAwait(false);
        var userId = userDocument.RootElement.TryGetProperty("id", out var userIdProperty) &&
            userIdProperty.ValueKind == JsonValueKind.String
                ? userIdProperty.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new GraphCallRecordsException("graphResponseInvalid", "Microsoft Graph returned an invalid user record.");
        }

        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"startDateTime ge {FormatGraphDate(fromUtc)} and startDateTime lt {FormatGraphDate(toUtc)} and participants_v2/any(p:p/id eq '{EscapeODataString(userId)}')");
        var listPath = $"communications/callRecords?$filter={Uri.EscapeDataString(filter)}&$select=id,startDateTime,endDateTime,type,modalities";
        var records = await GetCollectionAsync(
            listPath,
            token,
            MaxQualityRecords,
            cancellationToken).ConfigureAwait(false);
        var calls = new List<JsonElement>(records.Items.Count);
        var truncated = records.Truncated;

        foreach (var record in records.Items)
        {
            var id = record.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new GraphCallRecordsException(
                    "graphResponseInvalid",
                    "Microsoft Graph returned an invalid call record.");
            }

            var sessions = await GetCollectionAsync(
                $"communications/callRecords/{Uri.EscapeDataString(id)}/sessions?$expand=segments",
                token,
                MaxSessionsPerCall,
                cancellationToken).ConfigureAwait(false);
            truncated |= sessions.Truncated;
            calls.Add(JsonSerializer.SerializeToElement(new
            {
                id,
                startDateTime = ReadProperty(record, "startDateTime"),
                endDateTime = ReadProperty(record, "endDateTime"),
                type = ReadProperty(record, "type"),
                modalities = ReadProperty(record, "modalities"),
                sessions = sessions.Items,
            }));
        }

        return new GraphQualityCallData(userId, calls, truncated);
    }

    private async Task<string> AcquireTokenAsync(
        GraphTenantContext context,
        CancellationToken cancellationToken)
    {
        TenantCredential credential;
        try
        {
            credential = await credentialProvider.ResolveAsync(context.CredentialRef, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CredentialResolutionException ex)
        {
            throw new GraphCallRecordsException(
                "authenticationFailed",
                "The tenant credential could not be resolved.",
                ex);
        }
        catch (Exception ex)
        {
            throw new GraphCallRecordsException(
                "authenticationFailed",
                "The tenant credential could not be resolved.",
                ex);
        }

        using (credential.Certificate)
        {
            if (credential.TenantId != context.TenantId)
            {
                throw new GraphCallRecordsException(
                    "authenticationFailed",
                    "The resolved credential does not belong to the requested tenant.");
            }

            try
            {
                var tokenCredential = new ClientCertificateCredential(
                    context.TenantId.ToString(),
                    credential.ClientId,
                    credential.Certificate);
                var token = await tokenCredential.GetTokenAsync(TokenRequest, cancellationToken).ConfigureAwait(false);
                return token.Token;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not GraphCallRecordsException)
            {
                throw new GraphCallRecordsException(
                    "authenticationFailed",
                    "Microsoft Graph authentication failed.",
                    ex);
            }
        }
    }

    private static async Task<(IReadOnlyList<JsonElement> Items, bool Truncated)> GetCollectionAsync(
        string relativeOrAbsolutePath,
        string token,
        int maximumItems,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? nextPath = relativeOrAbsolutePath;

        while (nextPath is not null && items.Count < maximumItems)
        {
            using var document = await GetDocumentAsync(nextPath, token, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new GraphCallRecordsException(
                    "graphResponseInvalid",
                    "Microsoft Graph returned an invalid call-record collection.");
            }

            foreach (var item in value.EnumerateArray())
            {
                if (items.Count == maximumItems)
                {
                    break;
                }

                items.Add(item.Clone());
            }

            nextPath = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) &&
                nextLink.ValueKind == JsonValueKind.String
                    ? nextLink.GetString()
                    : null;
        }

        return (items, nextPath is not null);
    }

    private static async Task<JsonDocument> GetDocumentAsync(
        string relativeOrAbsolutePath,
        string token,
        CancellationToken cancellationToken)
    {
        var requestUri = ResolveGraphUri(relativeOrAbsolutePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "omit-values=nulls");

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GraphCallRecordsException(
                "graphRequestTimedOut",
                "Microsoft Graph timed out while processing the call-record request.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GraphCallRecordsException(
                "graphRequestFailed",
                "Microsoft Graph could not complete the call-record request.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapGraphFailure(response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new GraphCallRecordsException(
                    "graphResponseInvalid",
                    "Microsoft Graph returned malformed call-record data.",
                    ex);
            }
        }
    }

    private static Uri ResolveGraphUri(string relativeOrAbsolutePath)
    {
        if (!Uri.TryCreate(HttpClient.BaseAddress, relativeOrAbsolutePath, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, HttpClient.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
        {
            throw new GraphCallRecordsException(
                "graphResponseInvalid",
                "Microsoft Graph returned an invalid paging link.");
        }

        return uri;
    }

    private static GraphCallRecordsException MapGraphFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new("authenticationFailed", "Microsoft Graph authentication failed."),
        HttpStatusCode.Forbidden => new(
            "callRecordsPermissionDenied",
            "The Entra application requires the CallRecords.Read.All application permission with admin consent."),
        HttpStatusCode.NotFound => new("graphResourceNotFound", "The requested Microsoft Graph resource was not found."),
        HttpStatusCode.TooManyRequests => new("graphThrottled", "Microsoft Graph throttled the call-record request; retry later."),
        _ => new("graphRequestFailed", "Microsoft Graph could not complete the call-record request."),
    };

    private static string FormatGraphDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonElement? ReadProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}