# Complete solution tree

Updated after workflow removal on 2026-10-16. Build outputs, empty directories, local configuration and disposable test databases are omitted.

```text
BE_InviteMe/
|-- .config/
|   `-- dotnet-tools.json
|-- artifacts/
|   `-- migrations.sql
|-- database/
|   `-- inviteme_postgresql_schema_v3.sql
|-- docs/
|   |-- postman/
|   |   `-- InviteMe-Auth.postman_collection.json
|   |-- architecture-diagram-alignment.md
|   |-- architecture.md
|   |-- database.md
|   |-- solution-tree.md
|   `-- verification.md
|-- scripts/
|   |-- contracts/
|   |   `-- verify-invitation-configuration.cjs
|   `-- database/
|       `-- ReadSchemaCatalog.sql
|-- src/
|   |-- InviteMe.Api/
|   |   |-- Authentication/
|   |   |   |-- AccessTokenIssuer.cs
|   |   |   `-- CurrentUser.cs
|   |   |-- Configuration/
|   |   |   `-- JwtOptions.cs
|   |   |-- Endpoints/
|   |   |   |-- AccountEndpoints.cs
|   |   |   |-- ApplicationHealthEndpoints.cs
|   |   |   |-- AuditEndpoints.cs
|   |   |   `-- CoHostEndpoints.cs
|   |   |-- Extensions/
|   |   |   |-- ApiExtensions.cs
|   |   |   `-- AuthenticationExtensions.cs
|   |   |-- Hubs/
|   |   |   `-- WeddingHub.cs
|   |   |-- Middleware/
|   |   |   |-- ApiExceptionHandler.cs
|   |   |   `-- CorrelationMiddleware.cs
|   |   |-- Properties/
|   |   |   `-- launchSettings.json
|   |   |-- Serialization/
|   |   |   `-- OffsetDateTimeConverter.cs
|   |   |-- appsettings.example.json
|   |   |-- appsettings.json
|   |   |-- InviteMe.Api.csproj
|   |   `-- Program.cs
|   |-- InviteMe.Application/
|   |   |-- Common/
|   |   |   |-- Authorization/
|   |   |   |   `-- WeddingPermissionService.cs
|   |   |   |-- Behaviors/
|   |   |   |   `-- RequestValidation.cs
|   |   |   |-- Errors/
|   |   |   |   `-- ApplicationProblemException.cs
|   |   |   `-- Models/
|   |   |       |-- PagedResult.cs
|   |   |       `-- PageRequestValidator.cs
|   |   |-- Features/
|   |   |   |-- Audit/
|   |   |   |   `-- AuditContracts.cs
|   |   |   |-- Identity/
|   |   |   |   `-- Accounts/
|   |   |   |       `-- AccountHandler.cs
|   |   |   |-- System/
|   |   |   |   `-- GetApplicationHealth/
|   |   |   |       `-- GetApplicationHealthHandler.cs
|   |   |   `-- Weddings/
|   |   |       `-- CoHosts/
|   |   |           `-- CoHostContracts.cs
|   |   |-- Ports/
|   |   |   |-- Audit/
|   |   |   |   `-- IAuditReader.cs
|   |   |   |-- Authentication/
|   |   |   |   |-- IAccountStore.cs
|   |   |   |   `-- ICurrentUser.cs
|   |   |   |-- Authorization/
|   |   |   |   |-- IWeddingAccessReader.cs
|   |   |   |   `-- IWeddingPermissionService.cs
|   |   |   `-- Weddings/
|   |   |       `-- ICoHostStore.cs
|   |   |-- DependencyInjection.cs
|   |   `-- InviteMe.Application.csproj
|   |-- InviteMe.Domain/
|   |   |-- AI/
|   |   |   `-- AiGeneration.cs
|   |   |-- Audit/
|   |   |   `-- AuditLog.cs
|   |   |-- Billing/
|   |   |   |-- Plan.cs
|   |   |   `-- Subscription.cs
|   |   |-- CheckIn/
|   |   |   |-- CheckInRecord.cs
|   |   |   `-- WalkIn.cs
|   |   |-- Gifts/
|   |   |   |-- Gift.cs
|   |   |   `-- GiftMessage.cs
|   |   |-- Guests/
|   |   |   |-- GuestGroup.cs
|   |   |   |-- GuestNote.cs
|   |   |   |-- GuestParticipant.cs
|   |   |   `-- WeddingGuest.cs
|   |   |-- Identity/
|   |   |   |-- Permission.cs
|   |   |   |-- PlatformRoles.cs
|   |   |   `-- RefreshToken.cs
|   |   |-- Invitations/
|   |   |   |-- Invitation.cs
|   |   |   |-- InvitationDelivery.cs
|   |   |   |-- InvitationTemplate.cs
|   |   |   `-- TemplateSection.cs
|   |   |-- Notifications/
|   |   |   `-- Notification.cs
|   |   |-- Rsvps/
|   |   |   |-- CapacityRules.cs
|   |   |   |-- Rsvp.cs
|   |   |   |-- RsvpHistory.cs
|   |   |   `-- WaitlistEntry.cs
|   |   |-- Seating/
|   |   |   |-- ReceptionTable.cs
|   |   |   |-- Seat.cs
|   |   |   |-- SeatingAssignment.cs
|   |   |   |-- SeatingChangeLog.cs
|   |   |   |-- TableLifecycle.cs
|   |   |   `-- TableStatusHistory.cs
|   |   |-- Weddings/
|   |   |   |-- LoveStory.cs
|   |   |   |-- Venue.cs
|   |   |   |-- Wedding.cs
|   |   |   |-- WeddingAccess.cs
|   |   |   |-- WeddingEvent.cs
|   |   |   |-- WeddingMedia.cs
|   |   |   |-- WeddingMember.cs
|   |   |   |-- WeddingMemberPermission.cs
|   |   |   |-- WeddingPermission.cs
|   |   |   `-- WeddingSettings.cs
|   |   `-- InviteMe.Domain.csproj
|   `-- InviteMe.Infrastructure/
|       |-- Authentication/
|       |   |-- AccountStore.cs
|       |   |-- GoogleAuthOptions.cs
|       |   |-- GoogleTokenValidator.cs
|       |   |-- InvitationToken.cs
|       |   `-- RefreshTokenStore.cs
|       |-- Identity/
|       |   |-- ApplicationRole.cs
|       |   `-- ApplicationUser.cs
|       |-- Notifications/
|       |   `-- SmtpAccountEmailSender.cs
|       |-- Persistence/
|       |   |-- Adapters/
|       |   |   |-- AuditReader.cs
|       |   |   |-- CoHostStore.cs
|       |   |   `-- WeddingAccessReader.cs
|       |   |-- Configurations/
|       |   |   |-- AiGenerationConfiguration.cs
|       |   |   |-- AuditLogConfiguration.cs
|       |   |   |-- CheckInRecordConfiguration.cs
|       |   |   |-- GiftConfiguration.cs
|       |   |   |-- GiftMessageConfiguration.cs
|       |   |   |-- GuestGroupConfiguration.cs
|       |   |   |-- GuestNoteConfiguration.cs
|       |   |   |-- GuestParticipantConfiguration.cs
|       |   |   |-- IdentityConfigurations.cs
|       |   |   |-- InvitationConfiguration.cs
|       |   |   |-- InvitationDeliveryConfiguration.cs
|       |   |   |-- InvitationTemplateConfiguration.cs
|       |   |   |-- LoveStoryConfiguration.cs
|       |   |   |-- NotificationConfiguration.cs
|       |   |   |-- PermissionConfigurations.cs
|       |   |   |-- PlanConfiguration.cs
|       |   |   |-- ReceptionTableConfiguration.cs
|       |   |   |-- RefreshTokenConfiguration.cs
|       |   |   |-- RsvpConfiguration.cs
|       |   |   |-- RsvpHistoryConfiguration.cs
|       |   |   |-- SeatConfiguration.cs
|       |   |   |-- SeatingAssignmentConfiguration.cs
|       |   |   |-- SeatingChangeLogConfiguration.cs
|       |   |   |-- SubscriptionConfiguration.cs
|       |   |   |-- TableStatusHistoryConfiguration.cs
|       |   |   |-- TemplateSectionConfiguration.cs
|       |   |   |-- VenueConfiguration.cs
|       |   |   |-- WaitlistEntryConfiguration.cs
|       |   |   |-- WalkInConfiguration.cs
|       |   |   |-- WeddingConfigurations.cs
|       |   |   |-- WeddingEventConfiguration.cs
|       |   |   |-- WeddingGuestConfiguration.cs
|       |   |   |-- WeddingMediaConfiguration.cs
|       |   |   |-- WeddingReadModelConfigurations.cs
|       |   |   `-- WeddingSettingsConfiguration.cs
|       |   |-- Migrations/
|       |   |   |-- 20260924000100_ExistingSchemaBaseline.cs
|       |   |   |-- 20260924151913_AddIdentitySupport.cs
|       |   |   |-- 20260924151913_AddIdentitySupport.Designer.cs
|       |   |   |-- 20261002000100_AlignBusinessSchemaV2.cs
|       |   |   |-- 20261002141849_AdoptV2EntityMappings.cs
|       |   |   |-- 20261002141849_AdoptV2EntityMappings.Designer.cs
|       |   |   |-- 20261003074534_AddWorkspaceSupport.cs
|       |   |   |-- 20261003074534_AddWorkspaceSupport.Designer.cs
|       |   |   |-- 20261003120830_AddInvitationLifecycle.cs
|       |   |   |-- 20261003120830_AddInvitationLifecycle.Designer.cs
|       |   |   |-- 20261009033238_AddSeatingCheckInWorkflows.cs
|       |   |   |-- 20261009033238_AddSeatingCheckInWorkflows.Designer.cs
|       |   |   |-- 20261011104500_AddRsvpImmutableHistory.cs
|       |   |   |-- 20261011104500_AddRsvpImmutableHistory.Designer.cs
|       |   |   |-- 20261011114707_AddRefreshTokens.cs
|       |   |   |-- 20261011114707_AddRefreshTokens.Designer.cs
|       |   |   `-- InviteMeDbContextModelSnapshot.cs
|       |   |-- ReadModels/
|       |   |   `-- WeddingReadModels.cs
|       |   |-- Schema/
|       |   |   |-- Baseline.sql
|       |   |   |-- BaselineV2.sql
|       |   |   |-- IdentitySupport.sql
|       |   |   |-- InspectSchemaVersion.sql
|       |   |   |-- InvitationLifecycleSupport.sql
|       |   |   |-- MarkExistingBaseline.sql
|       |   |   |-- MarkExistingV2.sql
|       |   |   |-- RefreshTokenSupport.sql
|       |   |   |-- RsvpImmutableHistory.sql
|       |   |   |-- SchemaResources.cs
|       |   |   |-- SeatingCheckInWorkflows.sql
|       |   |   |-- UpgradeToV2.sql
|       |   |   `-- WorkspaceSupport.sql
|       |   |-- DatabaseOptions.cs
|       |   |-- InviteMeDbContext.cs
|       |   |-- InviteMeDbContextFactory.cs
|       |   `-- PostgreSqlHealthCheck.cs
|       |-- Properties/
|       |   `-- AssemblyInfo.cs
|       |-- DependencyInjection.cs
|       `-- InviteMe.Infrastructure.csproj
|-- tests/
|   |-- InviteMe.IntegrationTests/
|   |   |-- ApiFactory.cs
|   |   |-- ArchitectureBoundaryTests.cs
|   |   |-- AuditHistoryTests.cs
|   |   |-- AuthRefreshApiTests.cs
|   |   |-- BusinessMappingTests.cs
|   |   |-- CoHostPermissionCoverageTests.cs
|   |   |-- CoHostPermissionTests.cs
|   |   |-- DatabaseApiFactory.cs
|   |   |-- DeleteAccountApiTests.cs
|   |   |-- EmailConfirmationApiTests.cs
|   |   |-- ErrorHandlingTests.cs
|   |   |-- ForgotPasswordApiTests.cs
|   |   |-- GoogleLoginApiTests.cs
|   |   |-- HttpFoundationTests.cs
|   |   |-- InviteMe.IntegrationTests.csproj
|   |   |-- PostgreSqlFoundationTests.cs
|   |   `-- SchemaV2MigrationTests.cs
|   `-- InviteMe.UnitTests/
|       |-- AuthorizationTests.cs
|       |-- CapacityRulesTests.cs
|       |-- ChangePasswordValidationTests.cs
|       |-- EmailConfirmationValidationTests.cs
|       |-- GoogleLoginValidationTests.cs
|       |-- InvitationLifecycleTests.cs
|       |-- InviteMe.UnitTests.csproj
|       |-- PasswordResetValidationTests.cs
|       |-- PersistenceModelTests.cs
|       |-- RsvpHistoryTests.cs
|       `-- ValidationAndTokenTests.cs
|-- .dockerignore
|-- .editorconfig
|-- .env.example
|-- .gitignore
|-- compose.yaml
|-- Directory.Build.props
|-- Directory.Packages.props
|-- Dockerfile
|-- global.json
|-- InviteMe.sln
`-- README.md
```
