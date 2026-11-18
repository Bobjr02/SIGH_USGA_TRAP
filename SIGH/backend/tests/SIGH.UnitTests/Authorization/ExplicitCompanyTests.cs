using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Application.Options;
using SIGH.Application.Users.CreateUser;
using SIGH.Domain.Repositories;
using SIGH.Infrastructure.Services;
using Xunit;

namespace SIGH.UnitTests.Authorization;

/// <summary>Nada escolhe "a primeira empresa" sem avisar, e empresa não autorizada nunca vira 401.</summary>
public class ExplicitCompanyTests
{
    private static readonly CompanyInfo Usga = new(Guid.NewGuid(), "USGA", "USGA", "USGA");
    private static readonly CompanyInfo Trap = new(Guid.NewGuid(), "TRAP", "Trapiche", "TRAP");

    private static CompanyGrant Grant(CompanyInfo company) =>
        new(company, ["Teste"], new HashSet<string>(), Array.Empty<Guid>(), DateTimeOffset.UtcNow);

    private static AuthorizedCompanyProvider Provider(params CompanyInfo[] requestCompanies)
    {
        var context = new Mock<ICompanyContext>();
        context.Setup(c => c.GetRequestGrantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(requestCompanies.Select(Grant).ToList());
        return new AuthorizedCompanyProvider(context.Object);
    }

    [Fact]
    public async Task ACompanyOutsideTheRequest_IsNotFound_NeverUnauthorized()
    {
        var act = () => Provider(Usga).ResolveCompanyIdsAsync(Trap.Id);
        await act.Should().ThrowAsync<CompanyNotAccessibleException>();
        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WithoutCompanies_IsNotFound_NeverUnauthorized() =>
        await ((Func<Task>)(() => Provider().ResolveCompanyIdsAsync(null))).Should().ThrowAsync<CompanyNotAccessibleException>();

    [Fact]
    public async Task WithoutARequestedCompany_AllRequestCompaniesAreUsed_NotTheFirst() =>
        (await Provider(Usga, Trap).ResolveCompanyIdsAsync(null)).Should().Equal(Usga.Id, Trap.Id);

    [Fact]
    public async Task ARequestedCompanyOfTheRequest_IsTheOnlyOneUsed() =>
        (await Provider(Usga, Trap).ResolveCompanyIdsAsync(Trap.Id)).Should().Equal(Trap.Id);

    [Fact]
    public async Task CreatingAUser_WithoutCompanyContext_FailsInsteadOfFallingBackToUsga()
    {
        var service = new CreateUserService(
            new Mock<IApplicationDbContext>().Object, new Mock<IUserRepository>().Object, new Mock<IPasswordHasher>().Object,
            Options.Create(new PasswordOptions()));

        var act = () => service.CreateUserAsync(new CreateUserRequest("NOME", "a@sigh.test", "52998224725", "Senha@Forte123", []));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Contexto de empresa");
    }
}
