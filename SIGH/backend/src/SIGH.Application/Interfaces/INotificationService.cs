namespace SIGH.Application.Interfaces;

using SIGH.Application.Common.Models;
using SIGH.Application.Notifications.DTOs;
using SIGH.Application.Notifications.Queries;

public interface INotificationService
{
    Task<Result<NotificationDto>> CreateNotificationAsync(CreateNotificationRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<NotificationDto>> MarkAsReadAsync(Guid notificationId, IReadOnlyCollection<Guid> companyIds, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<int>> MarkAllAsReadAsync(IReadOnlyCollection<Guid> companyIds, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<NotificationDto>>> GetUserNotificationsAsync(IReadOnlyCollection<Guid> companyIds, Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<NotificationDto>>> GetUserNotificationsAsync(GetNotificationsQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<NotificationDto>>> GetUnreadNotificationsAsync(IReadOnlyCollection<Guid> companyIds, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<int>> GetUnreadCountAsync(IReadOnlyCollection<Guid> companyIds, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<NotificationDto>> SendNotificationAsync(SendNotificationRequestDto request, CancellationToken cancellationToken = default);
    Task<Result<int>> SendBatchNotificationsAsync(IEnumerable<SendNotificationRequestDto> requests, CancellationToken cancellationToken = default);
}
