namespace InviteMe.Application.Common.Errors;

public enum ProblemKind { Unauthenticated, Forbidden, NotFound, Conflict, BusinessRule }

// Message must be safe for public responses. Never pass provider exception messages here.
public sealed class ApplicationProblemException(ProblemKind kind, string code, string message)
    : Exception(message)
{
    public ProblemKind Kind { get; } = kind;
    public string Code { get; } = code;
}
