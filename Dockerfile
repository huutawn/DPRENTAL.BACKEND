FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY PaymentService/src/Common/PaymentService.Common.csproj PaymentService/src/Common/
COPY PaymentService/src/Domain/PaymentService.Domain.csproj PaymentService/src/Domain/
COPY PaymentService/src/Infrastructure/PaymentService.Infrastructure.csproj PaymentService/src/Infrastructure/
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

FROM build AS migrator
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN /tools/dotnet-ef migrations has-pending-model-changes --project PaymentService/src/Infrastructure/PaymentService.Infrastructure.csproj --startup-project PaymentService/src/Api/PaymentService.Api.csproj --configuration Release
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "PaymentService/src/Infrastructure/PaymentService.Infrastructure.csproj", "--startup-project", "PaymentService/src/Api/PaymentService.Api.csproj", "--configuration", "Release", "--no-build"]

FROM build AS migrator-integrity
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN /tools/dotnet-ef migrations has-pending-model-changes --project PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj --startup-project PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj --context IntegrityDbContext --configuration Release
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj", "--startup-project", "PaymentService/src/IntegrityHost/PaymentService.IntegrityHost.csproj", "--context", "IntegrityDbContext", "--configuration", "Release", "--no-build"]

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

# Distinct non-root user ID for IntegrityHost
USER 10002
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8081") or exit 1; print $s "GET /health HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "PaymentService.IntegrityHost.dll"]
