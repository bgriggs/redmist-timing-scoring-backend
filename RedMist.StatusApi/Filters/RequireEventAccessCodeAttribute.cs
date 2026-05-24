using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Services;

namespace RedMist.StatusApi.Filters;

/// <summary>
/// Validates the X-Event-Access-Code header against the per-event access code for private events.
/// Reads <c>eventId</c> from the action's route/query arguments; if the action takes no <c>eventId</c>,
/// the filter is a no-op. Returns 401 when the event is private and the header is missing or wrong.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireEventAccessCodeAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ActionArguments.TryGetValue("eventId", out var eventIdObj) || eventIdObj is not int eventId)
        {
            await next();
            return;
        }

        var validator = context.HttpContext.RequestServices.GetService(typeof(IEventAccessValidator)) as IEventAccessValidator;
        if (validator == null)
        {
            await next();
            return;
        }

        string? code = null;
        if (context.HttpContext.Request.Headers.TryGetValue(Consts.EVENT_ACCESS_CODE_HEADER, out var header))
            code = header.ToString();

        if (!await validator.ValidateAsync(eventId, code, context.HttpContext.RequestAborted))
        {
            context.Result = new UnauthorizedObjectResult("Access code required or invalid for this event.");
            return;
        }

        await next();
    }
}
