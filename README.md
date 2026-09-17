# AuthService

Service xác thực cho hệ thống DPRENTAL, xây dựng bằng .NET theo kiến trúc phân lớp.

## Cấu trúc

```text
AuthService/
  src/
    Common/       # Các primitive và concern dùng chung, không chứa nghiệp vụ
    Domain/       # Entity, repository contract và quy tắc domain
    Application/  # Use case, orchestration và application contract
    Api/          # HTTP API cho client/API gateway, composition root
  docs/           # Tài liệu kỹ thuật
docker-compose.yml
```

Hướng phụ thuộc: `Api -> Application -> Domain`; các lớp có thể dùng `Common`.
Infrastructure sẽ được đặt trong module sở hữu nó khi có nhu cầu thực tế, thay vì tạo sẵn một lớp rỗng.

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
docker compose up -d
```

PostgreSQL lắng nghe ở `localhost:5432`; Valkey (Redis-compatible) ở `localhost:6379`. `.env` không được commit.

Khi bắt đầu dùng database/cache, cấp connection strings cho API bằng .NET User Secrets theo hướng dẫn trong [docs](AuthService/docs/README.md). Không đặt password hoặc signing key vào `appsettings*.json`.

## Theo dõi trace phân tán

AuthService tiếp tục W3C `traceparent` do Gateway gửi tới và dùng OpenTelemetry để server span của AuthService thuộc cùng trace. Root trace được lấy mẫu 100% ở Development và 10% ở môi trường khác; quyết định lấy mẫu từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình endpoint đầy đủ trong environment. Không đưa token hay credential của collector vào source control:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Trace không được bổ sung JWT, authorization header, request/response body, claim hoặc private key làm attribute.
