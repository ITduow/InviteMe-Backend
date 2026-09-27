namespace InviteMe.Application.Abstractions.Authentication;

public interface ICurrentUser
{
    Guid? UserId { get; }
}
