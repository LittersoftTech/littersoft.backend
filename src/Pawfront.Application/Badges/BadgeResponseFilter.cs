using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Contracts.Badges;

namespace Pawfront.Application.Badges;

/// <summary>Resolves live badges once per response, including nested cards and lists.
/// Only opted-in display contracts receive flags. No document details leave the reader.</summary>
public sealed class BadgeResponseFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        var value = result is IValueHttpResult payload ? payload.Value : result;
        if (value is null) return result;
        await BadgeResponseEnricher.EnrichAsync(value,
            context.HttpContext.RequestServices.GetRequiredService<IBadgeReader>(), context.HttpContext.RequestAborted);
        return result;
    }
}

/// <summary>Shared by HTTP responses and the SignalR conversation header.</summary>
public static class BadgeResponseEnricher
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Children = new();
    public static async Task EnrichAsync(object value, IBadgeReader reader, CancellationToken cancellationToken)
    {
        var parents = new List<IParentBadgeTarget>();
        var pets = new List<IPetBadgeTarget>();
        var bookings = new List<IEventBookerBadgeTarget>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? node)
        {
            if (node is null || node is string || node.GetType().IsValueType || !visited.Add(node)) return;
            if (node is IParentBadgeTarget parent) parents.Add(parent);
            if (node is IPetBadgeTarget pet) pets.Add(pet);
            if (node is IEventBookerBadgeTarget booking) bookings.Add(booking);
            if (node is IEnumerable items) { foreach (var item in items) Visit(item); return; }
            var type = node.GetType();
            if (type.Assembly != typeof(IParentBadgeTarget).Assembly) return;
            foreach (var property in Children.GetOrAdd(type, t => t.GetProperties()
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.PropertyType.IsValueType && p.PropertyType != typeof(string)).ToArray()))
                Visit(property.GetValue(node));
        }
        Visit(value);
        if (parents.Count + pets.Count + bookings.Count == 0) return;
        var snapshot = await reader.ReadAsync(
            parents.Select(p => p.BadgeParentId).OfType<Guid>().Distinct().ToArray(),
            pets.Select(p => p.BadgePetId).OfType<Guid>().Distinct().ToArray(),
            bookings.Select(p => p.BadgeEventBookingId).OfType<Guid>().Distinct().ToArray(),
            cancellationToken);
        foreach (var p in parents) p.HasIdBadge = p.BadgeParentId is Guid id && snapshot.Parents.Contains(id);
        foreach (var p in pets) p.HasMicrochipBadge = p.BadgePetId is Guid id && snapshot.Pets.Contains(id);
        foreach (var b in bookings) b.HasIdBadge = b.BadgeEventBookingId is Guid id && snapshot.EventBookings.Contains(id);
    }
}
