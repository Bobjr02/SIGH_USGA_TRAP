using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIGH.Persistence.Configurations;

public class UserRoleConfiguration : AuditableEntityConfiguration<UserRole>
{
    public override void Configure(EntityTypeBuilder<UserRole> builder)
    {
        base.Configure(builder);

        builder.ToTable("UserRoles");

        // Um vínculo ATIVO por perfil e empresa; o retirado (exclusão lógica) fica na história e permite reatribuir.
        builder.HasIndex(ur => new { ur.UserId, ur.RoleId, ur.CompanyId })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("IX_UserRoles_UserId_RoleId_CompanyId");

        builder.HasIndex(ur => new { ur.CompanyId, ur.RoleId })
            .HasDatabaseName("IX_UserRoles_CompanyId_RoleId");

        builder.HasOne<SIGH.Domain.Employees.Entities.Company>()
            .WithMany()
            .HasForeignKey(ur => ur.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
