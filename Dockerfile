FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY CatalogService/src/Common/CatalogService.Common.csproj CatalogService/src/Common/
COPY CatalogService/src/Domain/CatalogService.Domain.csproj CatalogService/src/Domain/
COPY CatalogService/src/Application/CatalogService.Application.csproj CatalogService/src/Application/
COPY CatalogService/src/Api/CatalogService.Api.csproj CatalogService/src/Api/

RUN dotnet restore CatalogService/src/Api/CatalogService.Api.csproj

COPY CatalogService/ CatalogService/
RUN dotnet publish CatalogService/src/Api/CatalogService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "CatalogService.Api.dll"]

FROM build AS migrator
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "CatalogService/src/Domain/CatalogService.Domain.csproj", "--startup-project", "CatalogService/src/Api/CatalogService.Api.csproj", "--configuration", "Release", "--no-build"]
