using Microsoft.AspNetCore.Identity;

namespace Lst.Platform.Identity;

/// <summary>
/// Identity kullanıcısı. Tip, tek <see cref="Persistence.LogisticsDbContext"/> tabanı gerektirdiği için Platform'dadır;
/// roller, hub atamaları ve kimlik endpoint'leri Identity modülündedir.
/// </summary>
public sealed class LstUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
}
