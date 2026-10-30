using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Pawfront.Application.Notifications;
using Pawfront.Contracts.Common;
using Pawfront.Api.Auth;

namespace Pawfront.Api.Endpoints;

internal static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/notifications", async (
            HttpContext context, INotificationInboxReader reader,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken,
            int take = 20, string? cursor = null) =>
        {
            // The general host policy also accepts raw Google sign-in tokens.
            // Their subject is not a Firebase UID. Only a validated Firebase JWT
            // can select a Firestore inbox.
            var authentication = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (!authentication.Succeeded || authentication.Principal is null)
                return Results.Unauthorized();

            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var uid = FirebaseClaims.GetFirebaseUserId(authentication.Principal);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var page = await reader.ListAsync(NotificationAudience.Provider, uid,
                    take, cursor, timeout.Token);
                return ApiResults.Ok(page);
            }
            catch (ArgumentException exception)
            {
                return ApiResults.BadRequest("InvalidRequest", exception.Message);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                loggerFactory.CreateLogger("NotificationEndpoints").LogError(exception,
                    "Could not read the signed-in user's notification inbox.");
                return Results.Json(new ApiResponse<object>(false, null,
                    new ApiError("NotificationsUnavailable", "Notifications are temporarily unavailable. Please try again.")),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithTags("Notifications")
        .WithSummary("List the signed-in user's notifications, newest first.")
        .Produces<ApiResponse<NotificationInboxPage>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ApiResponse<object>>(StatusCodes.Status400BadRequest)
        .Produces<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable);
        builder.MapPost("/notifications/{notificationId}/read", (
            string notificationId, HttpContext context, INotificationReadStateStore store,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MarkReadAsync(notificationId, context, store, loggerFactory, cancellationToken))
            .WithTags("Notifications")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiResponse<object>>(StatusCodes.Status400BadRequest)
            .Produces<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable);
        builder.MapPost("/notifications/read-all", (
            HttpContext context, INotificationReadStateStore store,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MarkReadAsync(null, context, store, loggerFactory, cancellationToken))
            .WithTags("Notifications")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable);
        return builder;
    }

    private static async Task<IResult> MarkReadAsync(string? notificationId, HttpContext context,
        INotificationReadStateStore store, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var authentication = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || authentication.Principal is null)
            return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var uid = FirebaseClaims.GetFirebaseUserId(authentication.Principal);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (notificationId is null)
                await store.MarkAllReadAsync(NotificationAudience.Provider, uid, timeout.Token);
            else if (!await store.MarkReadAsync(NotificationAudience.Provider, uid, notificationId, timeout.Token))
                return Results.NotFound();
            return Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("NotificationEndpoints").LogError(exception,
                "Could not update the signed-in user's notification read status.");
            return Results.Json(new ApiResponse<object>(false, null,
                new ApiError("NotificationsUnavailable", "Notifications are temporarily unavailable. Please try again.")),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
