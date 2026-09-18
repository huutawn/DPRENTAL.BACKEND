FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AuthService.sln Directory.Build.props ./
COPY AuthService/src/Common/AuthService.Common.csproj AuthService/src/Common/
COPY AuthService/src/Domain/AuthService.Domain.csproj AuthService/src/Domain/
COPY AuthService/src/Application/AuthService.Application.csproj AuthService/src/Application/
COPY AuthService/src/Api/AuthService.Api.csproj AuthService/src/Api/
COPY AuthService/tests/Api.Tests/AuthService.Api.Tests.csproj AuthService/tests/Api.Tests/

RUN dotnet restore AuthService.sln

COPY AuthService/ AuthService/
RUN dotnet publish AuthService/src/Api/AuthService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "AuthService.Api.dll"]

FROM build AS migrator
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

WORKDIR /src
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "AuthService/src/Domain/AuthService.Domain.csproj", "--startup-project", "AuthService/src/Api/AuthService.Api.csproj", "--no-build"]
