# Local PostgreSQL integration tests

Start Docker Desktop, configure the repository `.env` from `.env.example`, and run from the repository root:

```powershell
docker compose up -d postgres
dotnet test RentalService.sln
dotnet test RentalService.sln --configuration Release
```

All PostgreSQL fixtures share `PostgresTestDatabaseHelper.BaseConnectionString`.
Connection settings are selected in this order:

1. `POSTGRESQL_TEST_CONNECTION_STRING` environment variable.
2. `RENTAL_TEST_POSTGRES_CONNECTION` environment variable.
3. Repository `.env`: `POSTGRES_PORT`, `POSTGRES_USER`, and `POSTGRES_PASSWORD`, using host `127.0.0.1` and maintenance database `postgres`.

The `.env` file is located relative to the compiled test assembly, so the same settings work in Visual Studio Test Explorer, `dotnet test`, and the pre-push hook. Keep `.env` untracked. An explicit environment variable overrides this file; remove stale overrides when switching local databases.

Database tests create uniquely named temporary databases, apply their required schema, and clean them up. The configured user needs permission to create databases. These tests do not use the application's database for fixtures. The local Docker PostgreSQL user has the required permission by default.

The application's database must be migrated separately using `dotnet ef database update` with its `ConnectionStrings__Postgres` configuration.
