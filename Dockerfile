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
FROM build AS migrator
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN /tools/dotnet-ef migrations has-pending-model-changes --project RentalService/src/Domain/RentalService.Domain.csproj --startup-project RentalService/src/Api/RentalService.Api.csproj --configuration Release
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "RentalService/src/Domain/RentalService.Domain.csproj", "--startup-project", "RentalService/src/Api/RentalService.Api.csproj", "--configuration", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new("127.0.0.1:8080") or exit 1; print $s "GET /health/ready HTTP/1.0\r\n\r\n"; my $r=<$s>; exit($r =~ /200/ ? 0 : 1)' || exit 1

ENTRYPOINT ["dotnet", "RentalService.Api.dll"]
