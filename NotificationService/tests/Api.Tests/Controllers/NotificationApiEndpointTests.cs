using System.Security.Claims;
using Dprental.NotificationService.Api.Controllers;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Services;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Controllers;

public sealed class NotificationApiEndpointTests
{
    private static NotificationServiceDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new NotificationServiceDbContext(options);
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid userId, string? tokenUse = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString())
        };

        if (!string.IsNullOrEmpty(tokenUse))
        {
            claims.Add(new Claim("u", tokenUse));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task NotificationsController_ServiceToken_ShouldBeForbidden()
    {
        using var db = CreateInMemoryDbContext();
        var notifRepo = new NotificationRepository(db);
        var queryService = new NotificationQueryService(notifRepo);
        var readService = new NotificationReadService(notifRepo);

        var controller = new NotificationsController(queryService, readService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUserPrincipal(Guid.NewGuid(), tokenUse: "svc") // Service token!
                }
            }
        };

        var response = await controller.GetNotifications();
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task NotificationsController_GetNotifications_ShouldReturnPagedResults()
    {
        using var db = CreateInMemoryDbContext();
        var notifRepo = new NotificationRepository(db);
        var queryService = new NotificationQueryService(notifRepo);
        var readService = new NotificationReadService(notifRepo);

        var userId = Guid.NewGuid();
        for (int i = 1; i <= 3; i++)
        {
            await notifRepo.AddAsync(new Notification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                NotificationType = "ORDER_CREATED",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = $"Đơn {i}",
                Body = $"Nội dung {i}",
                ResourceType = "RentalOrder",
                ResourceId = Guid.NewGuid().ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalOrderCreated",
                TemplateCode = "ORDER_CREATED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await db.SaveChangesAsync();

        var controller = new NotificationsController(queryService, readService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUserPrincipal(userId)
                }
            }
        };

        var response = await controller.GetNotifications(page: 1, pageSize: 2);
        var okResult = Assert.IsType<OkObjectResult>(response);
        var listDto = Assert.IsType<NotificationListResponseDto>(okResult.Value);

        Assert.Equal(3, listDto.TotalCount);
        Assert.Equal(2, listDto.Items.Count);
        Assert.True(listDto.HasNext);
        Assert.False(listDto.HasPrevious);
    }

    [Fact]
    public async Task NotificationsController_MarkAsRead_UnownedNotification_ReturnsNotFound()
    {
        using var db = CreateInMemoryDbContext();
        var notifRepo = new NotificationRepository(db);
        var queryService = new NotificationQueryService(notifRepo);
        var readService = new NotificationReadService(notifRepo);

        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await notifRepo.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = ownerId, // Owned by ownerId
            NotificationType = "ORDER_CREATED",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Title",
            Body = "Body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderCreated",
            TemplateCode = "ORDER_CREATED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = new NotificationsController(queryService, readService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUserPrincipal(attackerId) // Attacker tries to mark read
                }
            }
        };

        var response = await controller.MarkAsRead(notificationId);
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
    }

    [Fact]
    public async Task NotificationsController_MarkAllAsRead_AppliesServerCutoff()
    {
        using var db = CreateInMemoryDbContext();
        var notifRepo = new NotificationRepository(db);
        var queryService = new NotificationQueryService(notifRepo);

        var fixedTime = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var fakeTimeProvider = new FakeTimeProvider(fixedTime);

        var readService = new NotificationReadService(notifRepo, fakeTimeProvider);
        var userId = Guid.NewGuid();

        // Older notification before cutoff
        await notifRepo.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            NotificationType = "ORDER_CREATED",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Older",
            Body = "Older",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderCreated",
            TemplateCode = "ORDER_CREATED",
            TemplateVersion = "1.0",
            OccurredAt = fixedTime.AddMinutes(-10),
            CreatedAt = fixedTime.AddMinutes(-10)
        });

        // Newer notification created after cutoff
        await notifRepo.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "INFO",
            Title = "Newer",
            Body = "Newer",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = fixedTime.AddMinutes(5),
            CreatedAt = fixedTime.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var controller = new NotificationsController(queryService, readService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUserPrincipal(userId)
                }
            }
        };

        var response = await controller.MarkAllAsRead();
        var okResult = Assert.IsType<OkObjectResult>(response);
        var resultDto = Assert.IsType<MarkAllReadResponseDto>(okResult.Value);

        Assert.Equal(1, resultDto.MarkedCount);
        Assert.Equal(fixedTime, resultDto.CutoffUtc);

        var unread = await notifRepo.GetUnreadCountAsync(userId);
        Assert.Equal(1, unread); // The newer one remains unread!
    }

    [Fact]
    public async Task PreferencesController_GetAndUpdatePreferences()
    {
        using var db = CreateInMemoryDbContext();
        var prefRepo = new PreferenceRepository(db);
        var uow = new UnitOfWork(db);
        var service = new PreferenceService(prefRepo, uow);
        var userId = Guid.NewGuid();

        var controller = new PreferencesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUserPrincipal(userId)
                }
            }
        };

        // GET defaults: all 4 categories should be emailEnabled = true
        var getResponse = await controller.GetPreferences();
        var okGet = Assert.IsType<OkObjectResult>(getResponse);
        var prefsDto = Assert.IsType<PreferencesResponseDto>(okGet.Value);
        Assert.Equal(4, prefsDto.Preferences.Count);
        Assert.All(prefsDto.Preferences, p => Assert.True(p.EmailEnabled));

        // PUT update: turn off ORDER email
        var updateRequest = new UpdatePreferencesRequestDto(new[]
        {
            new PreferenceDto("ORDER", false),
            new PreferenceDto("PAYMENT", true)
        });

        var putResponse = await controller.UpdatePreferences(updateRequest);
        var okPut = Assert.IsType<OkObjectResult>(putResponse);
        var updatedDto = Assert.IsType<PreferencesResponseDto>(okPut.Value);

        var orderPref = updatedDto.Preferences.First(p => p.Category == "ORDER");
        Assert.False(orderPref.EmailEnabled);
    }
}
