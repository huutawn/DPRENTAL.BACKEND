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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "NotificationService.Api.dll"]
