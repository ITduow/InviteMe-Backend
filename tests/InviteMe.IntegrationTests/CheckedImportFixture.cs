using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.IntegrationTests;

/// <summary>Existing downstream fixtures explicitly choose CREATE_SEPARATE after the new check step.
/// Contract tests use SendAsync directly to verify missing checks, decisions and stale previews.</summary>
internal static class CheckedImportFixture
{
    private static readonly ConditionalWeakTable<HttpClient, ConcurrentDictionary<string, (string Source, PlanningImport Body)>> Attempts = new();
    public static async Task<HttpResponseMessage> SendCheckedImportAsync(this HttpClient client, HttpRequestMessage request)
    {
        var path = request.RequestUri!.ToString();
        if (!path.EndsWith("/imports", StringComparison.Ordinal)) throw new InvalidOperationException("Import fixture requires /imports.");
        var source = await request.Content!.ReadAsStringAsync();
        var input = JsonSerializer.Deserialize<PlanningImport>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var key = path + ":" + request.Headers.GetValues("Idempotency-Key").Single();
        var attempts = Attempts.GetOrCreateValue(client);
        if (attempts.TryGetValue(key, out var previous) && previous.Source == source) input = previous.Body;
        else
        {
            using var check = await client.PostAsJsonAsync(path + "/check", input);
            check.EnsureSuccessStatusCode();
            var preview = (await check.Content.ReadFromJsonAsync<PlanningImportPreview>())!;
            if (preview.Errors.Count == 0)
            {
                input = input with { PreviewToken = preview.PreviewToken,
                    Decisions = input.Rows!.Select(x => new PlanningImportDecision(x!.RowNumber, "CREATE_SEPARATE")).ToArray() };
                attempts[key] = (source, input);
            }
        }
        request.Content.Dispose(); request.Content = JsonContent.Create(input);
        return await client.SendAsync(request);
    }
}
