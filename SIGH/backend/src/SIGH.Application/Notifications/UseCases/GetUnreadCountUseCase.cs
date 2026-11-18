namespace SIGH.Application.Notifications.UseCases;

using SIGH.Application.Common.Models;
using SIGH.Application.Interfaces;

public class GetUnreadCountQuery
{
    public IReadOnlyCollection<Guid> CompanyIds { get; set; } = Array.Empty<Guid>();
    public Guid UserId { get; set; }
}

public interface IGetUnreadCountUseCase
{
    Task<Result<int>> ExecuteAsync(GetUnreadCountQuery query, CancellationToken cancellationToken = default);
}

public class GetUnreadCountUseCase : IGetUnreadCountUseCase
{
    private readonly INotificationService _notificationService;

    public GetUnreadCountUseCase(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task<Result<int>> ExecuteAsync(GetUnreadCountQuery query, CancellationToken cancellationToken = default)
    {
        if (query == null || query.CompanyIds is not { Count: > 0 } || query.UserId == Guid.Empty)
        {
            return Result<int>.Failure("Empresa e usuário são obrigatórios.", "REQUIRED_FIELDS_MISSING");
        }

        return await _notificationService.GetUnreadCountAsync(query.CompanyIds, query.UserId, cancellationToken);
    }
}
