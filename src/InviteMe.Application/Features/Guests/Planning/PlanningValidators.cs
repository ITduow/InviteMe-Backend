using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;

namespace InviteMe.Application.Features.Guests.Planning;

public static class PlanningText
{
    public static string Clean(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
    public static string Key(string? value) => Clean(value).ToUpperInvariant();
    public static bool Valid(string? value, int max, bool required = false, bool phone = false)
    {
        var text = Clean(value);
        return (!required || text.Length > 0) && text.Length <= max &&
            (phone || text.Length == 0 || !"=+@-".Contains(text[0]));
    }
    public static bool ValidSide(string? side) => side is null or "" or "BRIDE" or "GROOM" or "MUTUAL" or "UNSPECIFIED";
    public static bool ValidPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        var digits = Regex.Replace(text, "[^0-9]", "");
        return text.Length <= 30 && Regex.IsMatch(text, @"\A\+?[0-9][0-9 ().-]*\z") &&
            digits.Length is >= 7 and <= 15;
    }

    public static string PhoneKey(string? value) => Regex.Replace(value ?? "", "[^0-9]", "");
}

public sealed class PlanningRowValidator : AbstractValidator<PlanningRow>
{
    public PlanningRowValidator()
    {
        RuleFor(x => x.RowNumber).GreaterThan(0).WithErrorCode("INVALID_ROW_NUMBER");
        RuleFor(x => x.RecipientName).Must(x => PlanningText.Valid(x, 150, required: true)).WithErrorCode("INVALID_TEXT");
        RuleFor(x => x.Honorific).Must(x => PlanningText.Valid(x, 50)).WithErrorCode("INVALID_TEXT");
        RuleFor(x => x.Phone).Must(PlanningText.ValidPhone).WithErrorCode("INVALID_PHONE")
            .WithMessage("SĐT cần 7–15 chữ số, có thể có dấu + ở đầu, khoảng trắng, dấu chấm, gạch nối hoặc ngoặc.");
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop)
            .MaximumLength(254).WithErrorCode("INVALID_EMAIL")
            .EmailAddress().WithErrorCode("INVALID_EMAIL")
            .Must(x => PlanningText.Valid(x, 254)).WithErrorCode("INVALID_EMAIL")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Note).Must(x => PlanningText.Valid(x, 2000)).WithErrorCode("INVALID_TEXT");
        RuleFor(x => x.ExpectedPeople).Must(x => (x ?? 1) is >= 1 and <= 500).WithErrorCode("INVALID_EXPECTED_PEOPLE");
        RuleFor(x => x.Side).Must(PlanningText.ValidSide).WithErrorCode("INVALID_SIDE");
        When(x => x.Group is not null, () =>
        {
            RuleFor(x => x.Group).Must(g => g!.ExistingId is { } id
                ? id != Guid.Empty && g.NewName is null && !g.Confirmed
                : PlanningText.Valid(g.NewName, 100, required: true)).WithErrorCode("INVALID_GROUP");
            RuleFor(x => x.Group).Must(g => g!.ExistingId is not null || g.Confirmed).WithErrorCode("GROUP_CONFIRMATION_REQUIRED");
        });
    }
}

public sealed class PlanningImportValidator : AbstractValidator<PlanningImport>
{
    public PlanningImportValidator()
    {
        RuleFor(x => x.Rows).Must(x => x is { Count: >= 1 and <= 500 }).WithErrorCode("INVALID_ROWS");
        When(x => x.Rows is { Count: >= 1 and <= 500 }, () =>
        {
            RuleForEach(x => x.Rows).NotNull().WithErrorCode("INVALID_ROW");
            RuleForEach(x => x.Rows).SetValidator(new PlanningRowValidator()!);
            RuleFor(x => x.Rows).Custom((rows, context) =>
            {
                var seen = new HashSet<int>();
                for (var i = 0; i < rows!.Count; i++)
                    if (rows[i] is { } row && !seen.Add(row.RowNumber))
                        context.AddFailure(new ValidationFailure($"Rows[{i}].RowNumber", "Số dòng không được trùng.") { ErrorCode = "INVALID_ROW_NUMBER" });
            });
        });
    }

    // Translate FluentValidation paths to original spreadsheet lines without changing the HTTP contract.
    public static IReadOnlyList<ImportIssue> Issues(PlanningImport input, IEnumerable<ValidationFailure> failures) =>
        failures.Select(f =>
        {
            var match = Regex.Match(f.PropertyName, @"Rows\[(\d+)\](?:\.(\w+))?$");
            var index = match.Success ? int.Parse(match.Groups[1].Value) : -1;
            var row = index >= 0 && index < input.Rows?.Count ? input.Rows[index] : null;
            var property = match.Success && match.Groups[2].Success ? match.Groups[2].Value : "Rows";
            var field = char.ToLowerInvariant(property[0]) + property[1..];
            return new ImportIssue(row?.RowNumber ?? 0, field, f.ErrorCode, f.ErrorMessage);
        }).ToArray();
}

public sealed class PlanningGuestQueryValidator : AbstractValidator<PlanningGuestQuery>
{
    public PlanningGuestQueryValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 100000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.Side).Must(PlanningText.ValidSide);
    }
}

public sealed class CreatePlanningGuestValidator : AbstractValidator<CreatePlanningGuest>
{
    public CreatePlanningGuestValidator()
    {
        RuleFor(x => x.RecipientName).Must(x => PlanningText.Valid(x, 150, required: true)).WithErrorCode("INVALID_TEXT");
        RuleFor(x => x.Honorific).Must(x => PlanningText.Valid(x, 50)).WithErrorCode("INVALID_TEXT");
        RuleFor(x => x.InvitationKind).Must(x => x is "INDIVIDUAL" or "FAMILY" or "GROUP").WithErrorCode("INVALID_KIND");
        RuleFor(x => x.ExpectedPeople).InclusiveBetween(1, 500).WithErrorCode("INVALID_EXPECTED_PEOPLE");
        RuleFor(x => x.Side).Must(PlanningText.ValidSide).WithErrorCode("INVALID_SIDE");
        RuleFor(x => x.GroupId).Must(x => x is null || x != Guid.Empty).WithErrorCode("INVALID_GROUP");
        RuleFor(x => x.Phone).Must(PlanningText.ValidPhone).WithErrorCode("INVALID_PHONE")
            .WithMessage("SĐT cần 7–15 chữ số, có thể có dấu + ở đầu, khoảng trắng, dấu chấm, gạch nối hoặc ngoặc.");
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop)
            .MaximumLength(254).WithErrorCode("INVALID_EMAIL")
            .EmailAddress().WithErrorCode("INVALID_EMAIL")
            .Must(x => PlanningText.Valid(x, 254)).WithErrorCode("INVALID_EMAIL")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Note).Must(x => PlanningText.Valid(x, 2000)).WithErrorCode("INVALID_TEXT");
    }
}
