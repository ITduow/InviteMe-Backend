using FluentValidation;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Common.Authorization;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.System.GetApplicationHealth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<GetApplicationHealthHandler>();
        services.AddScoped(typeof(RequestValidation<>));
        services.AddScoped<GetApplicationHealthHandler>();
        services.AddScoped<InviteMe.Application.Features.Identity.Accounts.AccountHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Workspace.WorkspaceHandler>();
        services.AddScoped<InviteMe.Application.Features.Invitations.Lifecycle.InvitationHandler>();
        services.AddScoped<InviteMe.Application.Features.Invitations.Lifecycle.InvitationDeliveryProcessor>();
        services.AddScoped<InviteMe.Application.Features.Guests.ListWeddingGuests.ListWeddingGuestsHandler>();
        services.AddScoped<InviteMe.Application.Features.Guests.Import.GuestImportHandler>();
        services.AddScoped<InviteMe.Application.Features.Rsvps.Workflow.RsvpHandler>();
        services.AddScoped<InviteMe.Application.Features.Seating.Workflow.SeatingHandler>();
        services.AddScoped<InviteMe.Application.Features.Reception.ReceptionHandler>();
        services.AddScoped<InviteMe.Application.Features.Gifts.Workflow.GiftHandler>();
        services.AddScoped<InviteMe.Application.Features.Reports.WeddingReportHandler>();
        services.AddScoped<IWeddingPermissionService, WeddingPermissionService>();
        return services;
    }
}
