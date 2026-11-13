using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static Actor Me(HttpContext http, params UserType[] portals)
    {
        if (http.Items["actor"] is not Actor actor)
            throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
        if (portals.Length > 0 && !portals.Contains(actor.Portal))
            throw new RuleException("forbidden", "This account cannot use this API.", 403);
        return actor;
    }

    public static async Task<IResult> Page<T>(IQueryable<T> query, int? page, int? pageSize, Func<T, object> map, CancellationToken cancellationToken)
    {
        var rule = PageRules.Resolve(page, pageSize, out var resolvedPage, out var resolvedSize);
        RuleException.Require(rule);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((resolvedPage - 1) * resolvedSize).Take(resolvedSize).ToListAsync(cancellationToken);
        return Results.Ok(new { items = rows.Select(map).ToList(), page = resolvedPage, pageSize = resolvedSize, total });
    }

    public static async Task<IResult> PageWith<T>(IQueryable<T> query, int? page, int? pageSize, Func<List<T>, Task<List<object>>> project, CancellationToken cancellationToken)
    {
        var rule = PageRules.Resolve(page, pageSize, out var resolvedPage, out var resolvedSize);
        RuleException.Require(rule);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((resolvedPage - 1) * resolvedSize).Take(resolvedSize).ToListAsync(cancellationToken);
        return Results.Ok(new { items = await project(rows), page = resolvedPage, pageSize = resolvedSize, total });
    }

    public static IResult Created(string path, object body) => Results.Created(path, body);

    public static string Digits(string phone) => AccountRules.Digits(phone);
}

public sealed class PlainTextFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
            PlainText.Reject(argument);
        return await next(context);
    }
}
