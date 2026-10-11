using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Common.Errors;

namespace InviteMe.Api.Endpoints;

internal static class GuestFileImportEndpoints
{
    public static IEndpointRouteBuilder MapGuestFileImport(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guests").RequireAuthorization().WithTags("Guest Import");
        group.MapGet("/import-options", (Guid weddingId, GuestFileImportHandler handler, CancellationToken ct) => handler.OptionsAsync(weddingId, ct));
        group.MapPost("/import/preview", async (Guid weddingId, HttpRequest request, GuestFileImportHandler handler, CancellationToken ct) =>
        {
            await handler.OptionsAsync(weddingId, ct);
            const int maxBytes = 5 * 1024 * 1024;
            if (!request.HasFormContentType || request.ContentLength > maxBytes + 65536)
                throw new ApplicationProblemException(ProblemKind.BusinessRule, "IMPORT_UPLOAD", "Use multipart/form-data with one file, at most 5 MB.");
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions
                { MultipartBodyLengthLimit = maxBytes, ValueLengthLimit = 1024, ValueCountLimit = 10 }, ct);
            }
            catch (InvalidDataException)
            {
                throw new ApplicationProblemException(ProblemKind.BusinessRule, "IMPORT_UPLOAD", "Invalid multipart upload or file exceeds 5 MB.");
            }
            if (form.Files.Count != 1 || form.Files[0].Length is 0 or > maxBytes)
                throw new ApplicationProblemException(ProblemKind.BusinessRule, "IMPORT_UPLOAD", "Upload exactly one nonempty file, at most 5 MB.");
            var file = form.Files[0];
            using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
            return await handler.PreviewAsync(weddingId, file.FileName, stream.ToArray(), ct);
        });
        group.MapPost("/import/confirm", (Guid weddingId, ConfirmGuestFile input, GuestFileImportHandler handler, CancellationToken ct) => handler.ConfirmAsync(weddingId, input, ct));
        return endpoints;
    }
}
