FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY ReviewService/src/Common/ReviewService.Common.csproj ReviewService/src/Common/
COPY ReviewService/src/Domain/ReviewService.Domain.csproj ReviewService/src/Domain/
COPY ReviewService/src/Application/ReviewService.Application.csproj ReviewService/src/Application/
COPY ReviewService/src/Api/ReviewService.Api.csproj ReviewService/src/Api/

RUN dotnet restore ReviewService/src/Api/ReviewService.Api.csproj

COPY ReviewService/ ReviewService/
RUN dotnet publish ReviewService/src/Api/ReviewService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore
FROM build AS bundle-build
COPY --from=ef-tools /tools /tools
RUN /tools/dotnet-ef migrations has-pending-model-changes \
    --project ReviewService/src/Domain/ReviewService.Domain.csproj \
    --startup-project ReviewService/src/Api/ReviewService.Api.csproj \
    --configuration Release
RUN /tools/dotnet-ef migrations bundle \
    --project ReviewService/src/Domain/ReviewService.Domain.csproj \
    --startup-project ReviewService/src/Api/ReviewService.Api.csproj \
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
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "ReviewService.Api.dll"]
