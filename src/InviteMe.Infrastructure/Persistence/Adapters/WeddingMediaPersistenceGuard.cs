using System.Text.Json;
using InviteMe.Application.Features.Weddings.Setup;

namespace InviteMe.Infrastructure.Persistence.Adapters;

internal static class WeddingMediaPersistenceGuard
{
    public static async Task<bool> DocumentMediaExistsAsync(Guid weddingId, JsonElement document,
        IWeddingMediaStorage storage, IWeddingMediaCatalog catalog, CancellationToken ct)
    {
        foreach (var source in StringValues(document).Distinct(StringComparer.Ordinal))
            if (storage.TryGetOwnedFileName(weddingId, source, out var fileName) &&
                !await catalog.ContainsAsync(weddingId, fileName, ct))
                return false;
        return true;
    }

    public static async Task<bool> CoverMediaExistsAsync(Guid weddingId, string? source,
        IWeddingMediaStorage storage, IWeddingMediaCatalog catalog, CancellationToken ct) =>
        !storage.TryGetOwnedFileName(weddingId, source, out var fileName) ||
        await catalog.ContainsAsync(weddingId, fileName, ct);

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            if (element.GetString() is { } value) yield return value;
            yield break;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var value in StringValues(property.Value)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var value in StringValues(item)) yield return value;
        }
    }
}
