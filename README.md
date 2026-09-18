# AuthService

Service xác thực cho hệ thống DPRENTAL, xây dựng bằng .NET theo kiến trúc phân lớp.

## Cấu trúc

```text
AuthService/
  src/
    Common/       # Primitive dùng chung, gồm IRepo<TEntity, TKey> CRUD
    Domain/       # DTOs, Entities, IRepositories, Models, Repositories, Utils, Validators, persistence
    Application/  # Services, IServices, Security, Mapping và application logic
    Api/          # Controllers, Filters, HTTP/security composition root
  docs/           # Tài liệu kỹ thuật
docker-compose.yml
```

Hướng phụ thuộc: `Api -> Application -> Domain`; các lớp có thể dùng `Common`. API tham chiếu trực tiếp Domain chỉ tại composition root để đăng ký persistence.

## Chạy hạ tầng cục bộ

```bash
cp .env.example .env
docker compose up -d
```

PostgreSQL lắng nghe ở `localhost:5432`; Valkey (Redis-compatible) ở `localhost:6379`. `.env` không được commit.

Trước khi chạy API, cấp connection string, RSA JWT key ring bằng User Secrets hoặc secret manager theo [hướng dẫn local](docs/README.md#chạy-api-cục-bộ). Không đặt password hoặc signing key vào `appsettings*.json`.

## Theo dõi trace phân tán

AuthService tiếp tục W3C `traceparent` do Gateway gửi tới và dùng OpenTelemetry để server span của AuthService thuộc cùng trace. Root trace được lấy mẫu 100% ở Development và 10% ở môi trường khác; quyết định lấy mẫu từ caller luôn được giữ nguyên.

Để export trace qua OTLP HTTP/protobuf, cấu hình endpoint đầy đủ trong environment. Không đưa token hay credential của collector vào source control:

```text
Telemetry__Otlp__TracesEndpoint=https://otel-collector.internal/v1/traces
```

Trace không được bổ sung JWT, authorization header, request/response body, claim hoặc private key làm attribute.
