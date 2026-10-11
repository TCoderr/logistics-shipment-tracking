using Microsoft.AspNetCore.Identity;

namespace Lst.Platform.Identity;

public sealed class LstRole : IdentityRole<Guid>
{
    public LstRole()
    {
    }

    public LstRole(string roleName)
        : base(roleName)
    {
    }
}
