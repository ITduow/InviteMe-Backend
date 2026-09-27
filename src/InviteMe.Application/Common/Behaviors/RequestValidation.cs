using FluentValidation;

namespace InviteMe.Application.Common.Behaviors;

// Explicitly invoke at the beginning of a handler; no mediator or hidden pipeline.
public sealed class RequestValidation<T>(IEnumerable<IValidator<T>> validators)
{
    public async Task ValidateAsync(T request, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }
        if (failures.Count > 0)
            throw new ValidationException(failures);
    }
}
