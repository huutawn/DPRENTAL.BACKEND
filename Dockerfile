FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY RentalService/src/Common/RentalService.Common.csproj RentalService/src/Common/
COPY RentalService/src/Domain/RentalService.Domain.csproj RentalService/src/Domain/
COPY RentalService/src/Application/RentalService.Application.csproj RentalService/src/Application/
COPY RentalService/src/Api/RentalService.Api.csproj RentalService/src/Api/

RUN dotnet restore RentalService/src/Api/RentalService.Api.csproj

COPY RentalService/ RentalService/
RUN dotnet publish RentalService/src/Api/RentalService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore
FROM build AS bundle-build
COPY --from=ef-tools /tools /tools
RUN /tools/dotnet-ef migrations has-pending-model-changes \
    --project RentalService/src/Domain/RentalService.Domain.csproj \
    --startup-project RentalService/src/Api/RentalService.Api.csproj \
    --configuration Release
RUN /tools/dotnet-ef migrations bundle \
    --project RentalService/src/Domain/RentalService.Domain.csproj \
    --startup-project RentalService/src/Api/RentalService.Api.csproj \
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

ENTRYPOINT ["dotnet", "RentalService.Api.dll"]
