using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Lst.Modules.Identity;

/// <summary>Rol koşulu cookie claim'i ile değil, güncel DB rolüyle değerlendirilir.</summary>
public sealed class CurrentRoleRequirement(IReadOnlyCollection<string> roles) : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> Roles { get; } = roles;
}

internal sealed class CurrentRoleHandler(IActorAccessReader accessReader) : AuthorizationHandler<CurrentRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentRoleRequirement requirement)
    {
        var userId = context.User.GetUserId();
        if (userId is null)
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        var access = await accessReader.GetAsync(userId.Value, cancellationToken);
        if (access.HasAnyRole(requirement.Roles))
        {
            context.Succeed(requirement);
        }
    }
}
