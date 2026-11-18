namespace SIGH.Application.Notifications.UseCases;

using SIGH.Application.Common.Models;
using SIGH.Application.Interfaces;
using SIGH.Application.Notifications.DTOs;

public class GetUnreadNotificationsQuery
{
    public IReadOnlyCollection<Guid> CompanyIds { get; set; } = Array.Empty<Guid>();
    public Guid UserId { get; set; }
}

public interface IGetUnreadNotificationsUseCase
{
    Task<Result<IEnumerable<NotificationDto>>> ExecuteAsync(GetUnreadNotificationsQuery query, CancellationToken cancellationToken = default);
}

public class GetUnreadNotificationsUseCase : IGetUnreadNotificationsUseCase
{
    private readonly INotificationService _notificationService;

    public GetUnreadNotificationsUseCase(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task<Result<IEnumerable<NotificationDto>>> ExecuteAsync(GetUnreadNotificationsQuery query, CancellationToken cancellationToken = default)
    {
        if (query == null || query.CompanyIds is not { Count: > 0 } || query.UserId == Guid.Empty)
        {
            return Result<IEnumerable<NotificationDto>>.Failure("Empresa e usuário são obrigatórios.", "REQUIRED_FIELDS_MISSING");
        }

        return await _notificationService.GetUnreadNotificationsAsync(query.CompanyIds, query.UserId, cancellationToken);
    }
}
