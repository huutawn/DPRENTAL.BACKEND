# ==========================================
# Build Stage: Biên dịch và publish ứng dụng .NET 10
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Sao chép cấu hình build và project ApiGateway để cache layer restore dependencies
COPY Gateway/Directory.Build.props Gateway/
COPY Gateway/src/ApiGateway/ApiGateway.csproj Gateway/src/ApiGateway/

RUN dotnet restore Gateway/src/ApiGateway/ApiGateway.csproj

# Sao chép mã nguồn ApiGateway và thực hiện publish release
COPY Gateway/src/ApiGateway/ Gateway/src/ApiGateway/
WORKDIR /src/Gateway/src/ApiGateway
RUN dotnet publish -c Release -o /app/publish --no-restore

# ==========================================
# Runtime Stage: Image chạy tối giản và an toàn
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*


# Sao chép kết quả publish từ build stage
COPY --from=build /app/publish .

# Cấu hình cổng HTTP mặc định trong container
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Chạy với user không có quyền root (Non-root user)
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/health/ready || exit 1


# Khởi chạy ứng dụng API Gateway
ENTRYPOINT ["dotnet", "ApiGateway.dll"]