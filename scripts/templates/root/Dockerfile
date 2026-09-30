FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY __SERVICE_NAME__/src/Common/__SERVICE_NAME__.Common.csproj __SERVICE_NAME__/src/Common/
COPY __SERVICE_NAME__/src/Domain/__SERVICE_NAME__.Domain.csproj __SERVICE_NAME__/src/Domain/
COPY __SERVICE_NAME__/src/Application/__SERVICE_NAME__.Application.csproj __SERVICE_NAME__/src/Application/
COPY __SERVICE_NAME__/src/Api/__SERVICE_NAME__.Api.csproj __SERVICE_NAME__/src/Api/

RUN dotnet restore __SERVICE_NAME__/src/Api/__SERVICE_NAME__.Api.csproj

COPY __SERVICE_NAME__/ __SERVICE_NAME__/
RUN dotnet publish __SERVICE_NAME__/src/Api/__SERVICE_NAME__.Api.csproj \
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

ENTRYPOINT ["dotnet", "__SERVICE_NAME__.Api.dll"]
