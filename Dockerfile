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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "ReviewService.Api.dll"]
