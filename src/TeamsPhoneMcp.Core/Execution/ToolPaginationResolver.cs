using System.Text.Json;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.Core.Execution;

public sealed class ToolPaginationResolver(IContinuationTokenService tokenService)
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public ToolPaginationResolution Resolve(
        ToolManifest manifest,
        string tenantId,
        IReadOnlyDictionary<string, JsonElement> arguments,
        JsonElement filters,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(arguments);

        var declaresPageSize = manifest.Inputs.ContainsKey("pageSize");
        var declaresContinuationToken = manifest.Inputs.ContainsKey("continuationToken");
        if (!declaresPageSize && !declaresContinuationToken)
        {
            return ToolPaginationResolution.NotPaged();
        }

        if (!declaresPageSize || !declaresContinuationToken)
        {
            return ToolPaginationResolution.Fail("invalidPaginationContract");
        }

        var pageSize = DefaultPageSize;
        if (arguments.TryGetValue("pageSize", out var pageSizeValue) &&
            (!pageSizeValue.TryGetInt32(out pageSize) || pageSize is < 1 or > MaximumPageSize))
        {
            return ToolPaginationResolution.Fail("invalidPagination");
        }

        var offset = 0;
        if (arguments.TryGetValue("continuationToken", out var tokenValue))
        {
            if (tokenValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenValue.GetString()))
            {
                return ToolPaginationResolution.Fail("invalidContinuationToken");
            }

            var validation = tokenService.Validate(
                tokenValue.GetString()!,
                manifest.Id,
                tenantId,
                filters,
                nowUtc);
            if (!validation.IsValid || !validation.NextOffset.HasValue)
            {
                return ToolPaginationResolution.Fail(validation.ErrorCode ?? "invalidContinuationToken");
            }

            offset = validation.NextOffset.Value;
        }

        return ToolPaginationResolution.Success(new ToolPipelinePagination(pageSize, offset));
    }

    public string IssueContinuationToken(
        ToolManifest manifest,
        string tenantId,
        JsonElement filters,
        int nextOffset,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return tokenService.Issue(manifest.Id, tenantId, filters, nextOffset, nowUtc);
    }
}

public readonly record struct ToolPaginationResolution(
    bool IsValid,
    ToolPipelinePagination? Pagination,
    string? ErrorCode)
{
    public static ToolPaginationResolution Success(ToolPipelinePagination pagination) =>
        new(true, pagination, null);

    public static ToolPaginationResolution NotPaged() => new(true, null, null);

    public static ToolPaginationResolution Fail(string errorCode) => new(false, null, errorCode);
}