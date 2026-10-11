using Lst.Platform.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lst.Platform.Persistence;

public sealed class LogisticsDbContext : IdentityDbContext<LstUser, LstRole, Guid>
{
    private readonly IReadOnlyList<IModelContributor> _contributors;

    public LogisticsDbContext(DbContextOptions<LogisticsDbContext> options, IEnumerable<IModelContributor> contributors)
        : base(options)
    {
        _contributors = contributors.ToList();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<LstUser>(b =>
        {
            b.ToTable("users", Schemas.Identity);
            b.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        });
        builder.Entity<LstRole>().ToTable("roles", Schemas.Identity);
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", Schemas.Identity);
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", Schemas.Identity);
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", Schemas.Identity);
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", Schemas.Identity);
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", Schemas.Identity);

        builder.ApplyConfigurationsFromAssembly(typeof(LogisticsDbContext).Assembly);

        foreach (var contributor in _contributors)
        {
            contributor.Configure(builder);
        }
    }
}
