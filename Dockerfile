FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY PaymentService/src/Common/PaymentService.Common.csproj PaymentService/src/Common/
COPY PaymentService/src/Domain/PaymentService.Domain.csproj PaymentService/src/Domain/
COPY PaymentService/src/Application/PaymentService.Application.csproj PaymentService/src/Application/
COPY PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj PaymentService/src/IntegrityHost/
COPY PaymentService/src/Api/PaymentService.Api.csproj PaymentService/src/Api/

RUN dotnet restore PaymentService/src/Api/PaymentService.Api.csproj
RUN dotnet restore PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj

COPY PaymentService/ PaymentService/

RUN dotnet publish PaymentService/src/Api/PaymentService.Api.csproj \
    --configuration Release \
    --output /app/publish/api \
    --no-restore

RUN dotnet publish PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj \
    --configuration Release \
    --output /app/publish/integrity \
    --no-restore

FROM build AS bundle-build
COPY --from=ef-tools /tools /tools
RUN /tools/dotnet-ef migrations has-pending-model-changes \
    --project PaymentService/src/Domain/PaymentService.Domain.csproj \
    --startup-project PaymentService/src/Api/PaymentService.Api.csproj \
    --context PaymentServiceDbContext \
    --configuration Release
RUN /tools/dotnet-ef migrations bundle \
    --project PaymentService/src/Domain/PaymentService.Domain.csproj \
    --startup-project PaymentService/src/Api/PaymentService.Api.csproj \
    --context PaymentServiceDbContext \
    --configuration Release \
    --no-build \
    --output /app/payment-efbundle
RUN /tools/dotnet-ef migrations has-pending-model-changes \
    --project PaymentService/src/Domain/PaymentService.Domain.csproj \
    --startup-project PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj \
    --context IntegrityDbContext \
    --configuration Release
RUN /tools/dotnet-ef migrations bundle \
    --project PaymentService/src/Domain/PaymentService.Domain.csproj \
    --startup-project PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj \
    --context IntegrityDbContext \
    --configuration Release \
    --no-build \
    --output /app/integrity-efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
RUN sed -i 's/Components: .*/Components: main/' /etc/apt/sources.list.d/ubuntu.sources \
    && sed -i 's/Suites: noble noble-updates noble-backports/Suites: noble noble-updates/' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && apt-get install --yes --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=bundle-build /app/payment-efbundle ./efbundle
COPY --from=build /app/publish/api/appsettings*.json ./
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator-integrity
WORKDIR /app
RUN sed -i 's/Components: .*/Components: main/' /etc/apt/sources.list.d/ubuntu.sources \
    && sed -i 's/Suites: noble noble-updates noble-backports/Suites: noble noble-updates/' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && apt-get install --yes --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=bundle-build /app/integrity-efbundle ./efbundle
COPY --from=build /app/publish/integrity/appsettings*.json ./
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish/api ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "PaymentService.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-integrity
WORKDIR /app

COPY --from=build /app/publish/integrity ./

ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081

USER 10002
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8081") or exit 1; print $s "GET /health HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "PaymentService.IntegrityHost.dll"]
