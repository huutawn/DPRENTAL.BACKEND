FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ef-tools
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY MediaService/src/Common/MediaService.Common.csproj MediaService/src/Common/
COPY MediaService/src/Domain/MediaService.Domain.csproj MediaService/src/Domain/
COPY MediaService/src/Application/MediaService.Application.csproj MediaService/src/Application/
COPY MediaService/src/Api/MediaService.Api.csproj MediaService/src/Api/

RUN dotnet restore MediaService/src/Api/MediaService.Api.csproj

COPY MediaService/ MediaService/
RUN dotnet publish MediaService/src/Api/MediaService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM build AS migrator
COPY --from=ef-tools /tools /tools
ENV PATH="${PATH}:/tools"
ENTRYPOINT ["/tools/dotnet-ef"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "MediaService.Api.dll"]
