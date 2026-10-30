using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Contracts.Common;
using Pawfront.Contracts.Providers;
namespace Pawfront.Application.ProviderTrust;

/// <summary>Only public profile and search results receive trust badges. Batch per search page.</summary>
public sealed class ProviderTrustBadgeFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        var value = (result as IValueHttpResult)?.Value;
        var service = context.HttpContext.RequestServices.GetRequiredService<IProviderTrustService>();
        if (value is ApiResponse<ProviderPublicProfileResponse> { Data: { } profile })
        {
            var badges = await service.GetBadgesAsync([profile.ProviderId], context.HttpContext.RequestAborted);
            profile.Badge = badges.GetValueOrDefault(profile.ProviderId);
        }
        else if (value is ApiResponse<ProviderSearchResultResponse[]> { Data: { } cards })
        {
            var badges = await service.GetBadgesAsync(cards.Select(c => c.ProviderId).Distinct().ToArray(), context.HttpContext.RequestAborted);
            foreach (var card in cards) card.Badge = badges.GetValueOrDefault(card.ProviderId);
        }
        return result;
    }
}
