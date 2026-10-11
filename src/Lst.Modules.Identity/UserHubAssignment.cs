using Lst.Platform.Identity;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Identity;

/// <summary>identity.user_hub_assignments: hub görevlisinin yetkili olduğu hub'lar. Yetki her istekte bu tablodan okunur.</summary>
public sealed class UserHubAssignment
{
    private UserHubAssignment()
    {
    }

    public UserHubAssignment(Guid userId, Guid hubId, DateTimeOffset assignedAt, Guid? assignedByUserId)
    {
        UserId = userId;
        HubId = hubId;
        AssignedAt = assignedAt;
        AssignedByUserId = assignedByUserId;
    }

    public Guid UserId { get; private set; }

    public Guid HubId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public Guid? AssignedByUserId { get; private set; }
}

internal sealed class IdentityModelContributor : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserHubAssignment>(b =>
        {
            b.ToTable("user_hub_assignments", Schemas.Identity);
            b.HasKey(a => new { a.UserId, a.HubId });
            b.HasOne<LstUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Hub>().WithMany().HasForeignKey(a => a.HubId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LstUser>().WithMany().HasForeignKey(a => a.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.HubId).HasDatabaseName("ix_user_hub_assignments_hub_id");
        });
    }
}
