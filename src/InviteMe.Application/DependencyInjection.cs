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
        services.AddScoped<InviteMe.Application.Features.Weddings.CoHosts.CoHostHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.CoHosts.CoHostInvitationHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.WeddingSetupHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.WeddingListHandler>();
        services.AddScoped<InviteMe.Application.Features.Audit.AuditHandler>();
        services.AddScoped<InviteMe.Application.Features.Guests.Planning.GuestPlanningHandler>();
        services.AddScoped<InviteMe.Application.Features.Invitations.InvitationWordingHandler>();
        services.AddScoped<InviteMe.Application.Features.Invitations.Messaging.InvitationMessagingHandler>();
        services.AddScoped<InviteMe.Application.Features.Invitations.InvitationDesignHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Website.WeddingWebsiteHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Website.StoryWishHandler>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.WeddingMediaHandler>();
        services.AddScoped<InviteMe.Application.Features.Seating.Planning.PlanningSeatingHandler>();
        services.AddScoped<InviteMe.Application.Features.Guests.Access.GuestAccessHandler>();
        services.AddScoped<InviteMe.Application.Features.Guests.Reception.ReceptionHandler>();
        services.AddScoped<InviteMe.Application.Features.Rsvps.RsvpHandler>();
        services.AddScoped<InviteMe.Application.Features.Rsvps.RsvpDashboardHandler>();
        services.AddScoped<InviteMe.Application.Features.Notifications.NotificationHandler>();
        services.AddScoped<InviteMe.Application.Features.Rsvps.RsvpReminderHandler>();
        services.AddScoped<InviteMe.Application.Features.Rsvps.RsvpReminderProcessor>();
        services.AddScoped<InviteMe.Application.Features.Gifts.GiftGuestHandler>();
        services.AddScoped<InviteMe.Application.Features.Gifts.GiftHostHandler>();
        services.AddScoped<InviteMe.Application.Features.Gifts.GiftWebhookHandler>();
        services.AddScoped<InviteMe.Application.Features.Billing.PlanHandler>();
        services.AddScoped<InviteMe.Application.Features.Billing.PlanOrderHandler>();
        services.AddScoped<InviteMe.Application.Features.Billing.AdminPlanHandler>();
        services.AddScoped<InviteMe.Application.Features.Billing.SePayWebhookDispatcher>();
        services.AddScoped<IWeddingPermissionService, WeddingPermissionService>();
        services.AddScoped<InviteMe.Application.Features.AI.ThankYouTemplates.ThankYouTemplateHandler>();
        services.AddScoped<InviteMe.Application.Features.AI.ThankYouMessages.ThankYouMessageHandler>();
        services.AddSingleton<InviteMe.Application.Ports.AI.IThankYouTemplateRenderer,
            InviteMe.Application.Features.AI.ThankYouTemplates.ThankYouTemplateRenderer>();
        return services;
    }
}
