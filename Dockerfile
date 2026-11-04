FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY NotificationService/src/Common/NotificationService.Common.csproj NotificationService/src/Common/
COPY NotificationService/src/Domain/NotificationService.Domain.csproj NotificationService/src/Domain/
COPY NotificationService/src/Application/NotificationService.Application.csproj NotificationService/src/Application/
COPY NotificationService/src/Api/NotificationService.Api.csproj NotificationService/src/Api/

RUN dotnet restore NotificationService/src/Api/NotificationService.Api.csproj

COPY NotificationService/ NotificationService/
RUN dotnet publish NotificationService/src/Api/NotificationService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM build AS bundle-build
COPY --from=ef-tools /tools /tools
RUN /tools/dotnet-ef migrations has-pending-model-changes \
    --project NotificationService/src/Domain/NotificationService.Domain.csproj \
    --startup-project NotificationService/src/Api/NotificationService.Api.csproj \
    --configuration Release
RUN /tools/dotnet-ef migrations bundle \
    --project NotificationService/src/Domain/NotificationService.Domain.csproj \
    --startup-project NotificationService/src/Api/NotificationService.Api.csproj \
    --configuration Release \
    --no-build \
    --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
RUN sed -i 's/Components: .*/Components: main/' /etc/apt/sources.list.d/ubuntu.sources \
    && sed -i 's/Suites: noble noble-updates noble-backports/Suites: noble noble-updates/' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && apt-get install --yes --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=bundle-build /app/efbundle ./
COPY --from=build /app/publish/appsettings*.json ./
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && echo -e 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && grep -q '200' <&3"]

ENTRYPOINT ["dotnet", "NotificationService.Api.dll"]
