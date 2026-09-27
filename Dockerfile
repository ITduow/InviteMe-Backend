FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/InviteMe.Api/InviteMe.Api.csproj src/InviteMe.Api/
COPY src/InviteMe.Application/InviteMe.Application.csproj src/InviteMe.Application/
COPY src/InviteMe.Domain/InviteMe.Domain.csproj src/InviteMe.Domain/
COPY src/InviteMe.Infrastructure/InviteMe.Infrastructure.csproj src/InviteMe.Infrastructure/
RUN dotnet restore src/InviteMe.Api/InviteMe.Api.csproj
COPY src/ src/
RUN dotnet publish src/InviteMe.Api/InviteMe.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "InviteMe.Api.dll"]
