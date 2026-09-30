#!/usr/bin/env python3
"""
Tạo base service theo cấu trúc AuthService cho hệ sinh thái DPRENTAL.

Sử dụng:
    python3 scripts/create-service.py <ServiceName>
    hoặc:
    /path/to/create-service.py <ServiceName>

Yêu cầu:
    - Tên service phải theo định dạng PascalCaseService (ví dụ: CatalogService, OrderService).
    - Thư mục đích được sinh tại thư mục đang đứng khi gọi lệnh (cwd).
    - Báo lỗi nếu thư mục đích đã tồn tại.
"""

from __future__ import annotations

import os
import re
import sys
import tempfile
import uuid
from hashlib import sha256
from pathlib import Path

# Toàn bộ 43 đường dẫn thư mục được Git theo dõi trong AuthService
TRACKED_DIRECTORIES_TEMPLATE: list[str] = [
    ".githooks",
    "{service_name}",
    "{service_name}/src",
    "{service_name}/src/Api",
    "{service_name}/src/Api/Background",
    "{service_name}/src/Api/Clients",
    "{service_name}/src/Api/Controllers",
    "{service_name}/src/Api/Exceptions",
    "{service_name}/src/Api/Filters",
    "{service_name}/src/Api/Observability",
    "{service_name}/src/Api/Properties",
    "{service_name}/src/Api/Routing",
    "{service_name}/src/Api/Security",
    "{service_name}/src/Application",
    "{service_name}/src/Application/Clients",
    "{service_name}/src/Application/DTOs",
    "{service_name}/src/Application/DTOs/Auth",
    "{service_name}/src/Application/DTOs/Media",
    "{service_name}/src/Application/DTOs/Organization",
    "{service_name}/src/Application/DTOs/User",
    "{service_name}/src/Application/DependencyInjection",
    "{service_name}/src/Application/IServices",
    "{service_name}/src/Application/Mapping",
    "{service_name}/src/Application/Options",
    "{service_name}/src/Application/Security",
    "{service_name}/src/Application/Security/Jwt",
    "{service_name}/src/Application/Services",
    "{service_name}/src/Application/Validation",
    "{service_name}/src/Application/Validators",
    "{service_name}/src/Common",
    "{service_name}/src/Common/Enums",
    "{service_name}/src/Common/Utils",
    "{service_name}/src/Domain",
    "{service_name}/src/Domain/Entities",
    "{service_name}/src/Domain/IRepositories",
    "{service_name}/src/Domain/Migrations",
    "{service_name}/src/Domain/Repositories",
    "{service_name}/src/Domain/ValueObjects",
    "{service_name}/tests",
    "{service_name}/tests/Api.Tests",
    "docs",
    "scripts",
    "scripts/tests",
]


def validate_service_name(name: str) -> None:
    """Kiểm tra tên service theo định dạng PascalCaseService."""
    if not name or len(name) <= 7 or not name.endswith("Service"):
        sys.stderr.write(
            f"Lỗi: Tên service phải có định dạng PascalCaseService (kết thúc bằng 'Service' và có tiền tố, ví dụ: CatalogService, OrderService). Giá trị nhận được: '{name}'\n"
        )
        sys.exit(1)

    pattern = r"^[A-Z][a-zA-Z0-9]+Service$"
    if not re.match(pattern, name):
        sys.stderr.write(
            f"Lỗi: Tên service '{name}' không đúng định dạng PascalCaseService. Chỉ cho phép ký tự chữ và số, bắt đầu bằng chữ hoa.\n"
        )
        sys.exit(1)


def generate_guid() -> str:
    """Sinh GUID ngẫu nhiên viết hoa dùng cho Visual Studio solution file."""
    return str(uuid.uuid4()).upper()


def main() -> None:
    if len(sys.argv) != 2 or sys.argv[1] in ("-h", "--help"):
        sys.stderr.write(
            "Cách dùng: python3 create-service.py <ServiceName>\n"
            "Ví dụ: python3 create-service.py CatalogService\n"
        )
        sys.exit(1)

    service_name = sys.argv[1]
    validate_service_name(service_name)

    # Thư mục làm việc hiện tại khi gọi script
    cwd = Path.cwd()
    target_dir = cwd / service_name

    if target_dir.exists() or target_dir.is_symlink():
        sys.stderr.write(f"Lỗi: Thư mục đích đã tồn tại: {target_dir}\n")
        sys.exit(1)

    # Tên viết thường bỏ 'Service' (ví dụ: CatalogService -> catalog)
    short_name = service_name[:-7].lower()
    service_name_lower = service_name.lower()
    port_seed = int.from_bytes(sha256(service_name.encode("ascii")).digest()[:4], "big")
    postgres_port = str(20000 + port_seed % 10000)
    http_port = str(40000 + port_seed % 10000)
    https_port = str(50000 + port_seed % 10000)

    # Thư mục chứa template bên trong repo
    script_dir = Path(__file__).resolve().parent
    templates_dir = script_dir / "templates"
    if not templates_dir.is_dir():
        sys.stderr.write(f"Lỗi: Không tìm thấy thư mục templates tại {templates_dir}\n")
        sys.exit(1)

    # Chuẩn bị các biến thay thế trong template.
    replacements: dict[str, str] = {
        "__SERVICE_NAME__": service_name,
        "__SHORT_NAME__": short_name,
        "__SERVICE_NAME_LOWER__": service_name_lower,
        "__POSTGRES_PORT__": postgres_port,
        "__HTTP_PORT__": http_port,
        "__HTTPS_PORT__": https_port,
        "__GUID_FOLDER_SVC__": generate_guid(),
        "__GUID_FOLDER_SRC__": generate_guid(),
        "__GUID_FOLDER_COMMON__": generate_guid(),
        "__GUID_FOLDER_DOMAIN__": generate_guid(),
        "__GUID_FOLDER_APPLICATION__": generate_guid(),
        "__GUID_FOLDER_API__": generate_guid(),
        "__GUID_FOLDER_TESTS__": generate_guid(),
        "__GUID_FOLDER_APITESTS__": generate_guid(),
        "__GUID_PRJ_COMMON__": generate_guid(),
        "__GUID_PRJ_DOMAIN__": generate_guid(),
        "__GUID_PRJ_APPLICATION__": generate_guid(),
        "__GUID_PRJ_API__": generate_guid(),
        "__GUID_PRJ_TESTS__": generate_guid(),
    }

    # Danh sách các file template và đường dẫn đích.
    template_files_map: list[tuple[Path, Path]] = [
        # Root configs
        (templates_dir / "root" / ".dockerignore", target_dir / ".dockerignore"),
        (templates_dir / "root" / ".env.example", target_dir / ".env.example"),
        (templates_dir / "root" / ".gitignore", target_dir / ".gitignore"),
        (templates_dir / "root" / "Directory.Build.props", target_dir / "Directory.Build.props"),
        (templates_dir / "root" / "Directory.Packages.props", target_dir / "Directory.Packages.props"),
        (templates_dir / "root" / "Dockerfile", target_dir / "Dockerfile"),
        (templates_dir / "root" / "README.md", target_dir / "README.md"),
        (templates_dir / "root" / "docker-compose.yml", target_dir / "docker-compose.yml"),
        (templates_dir / "root" / "solution.sln", target_dir / f"{service_name}.sln"),
        # Git hooks
        (templates_dir / "githooks" / "pre-push", target_dir / ".githooks" / "pre-push"),
        # Scripts
        (templates_dir / "scripts" / "pre-push-check.sh", target_dir / "scripts" / "pre-push-check.sh"),
        (templates_dir / "scripts" / "setup-git-hooks.sh", target_dir / "scripts" / "setup-git-hooks.sh"),
        (templates_dir / "scripts" / "setup-git-hooks.ps1", target_dir / "scripts" / "setup-git-hooks.ps1"),
        (templates_dir / "scripts" / "tests" / "pre-push-check.test.sh", target_dir / "scripts" / "tests" / "pre-push-check.test.sh"),
        # Docs
        (templates_dir / "docs" / "README.md", target_dir / "docs" / "README.md"),
        # Api
        (templates_dir / "service" / "src" / "Api" / "appsettings.json", target_dir / service_name / "src" / "Api" / "appsettings.json"),
        (templates_dir / "service" / "src" / "Api" / "appsettings.Development.json", target_dir / service_name / "src" / "Api" / "appsettings.Development.json"),
        (templates_dir / "service" / "src" / "Api" / "appsettings.Production.json", target_dir / service_name / "src" / "Api" / "appsettings.Production.json"),
        (templates_dir / "service" / "src" / "Api" / "Program.cs", target_dir / service_name / "src" / "Api" / "Program.cs"),
        (templates_dir / "service" / "src" / "Api" / "Api.csproj", target_dir / service_name / "src" / "Api" / f"{service_name}.Api.csproj"),
        (templates_dir / "service" / "src" / "Api" / "Properties" / "AssemblyInfo.cs", target_dir / service_name / "src" / "Api" / "Properties" / "AssemblyInfo.cs"),
        (templates_dir / "service" / "src" / "Api" / "Properties" / "launchSettings.json", target_dir / service_name / "src" / "Api" / "Properties" / "launchSettings.json"),
        # Application
        (templates_dir / "service" / "src" / "Application" / "Application.csproj", target_dir / service_name / "src" / "Application" / f"{service_name}.Application.csproj"),
        (templates_dir / "service" / "src" / "Application" / "ApplicationAssemblyMarker.cs", target_dir / service_name / "src" / "Application" / "ApplicationAssemblyMarker.cs"),
        # Common
        (templates_dir / "service" / "src" / "Common" / "Common.csproj", target_dir / service_name / "src" / "Common" / f"{service_name}.Common.csproj"),
        (templates_dir / "service" / "src" / "Common" / "CommonAssemblyMarker.cs", target_dir / service_name / "src" / "Common" / "CommonAssemblyMarker.cs"),
        (templates_dir / "service" / "src" / "Common" / "Result.cs", target_dir / service_name / "src" / "Common" / "Result.cs"),
        # Domain
        (templates_dir / "service" / "src" / "Domain" / "Domain.csproj", target_dir / service_name / "src" / "Domain" / f"{service_name}.Domain.csproj"),
        (templates_dir / "service" / "src" / "Domain" / "DomainAssemblyMarker.cs", target_dir / service_name / "src" / "Domain" / "DomainAssemblyMarker.cs"),
        (templates_dir / "service" / "src" / "Domain" / "Repositories" / "DbContext.cs", target_dir / service_name / "src" / "Domain" / "Repositories" / f"{service_name}DbContext.cs"),
        (templates_dir / "service" / "src" / "Domain" / "Repositories" / "DomainRepositoriesServiceCollectionExtensions.cs", target_dir / service_name / "src" / "Domain" / "Repositories" / "DomainRepositoriesServiceCollectionExtensions.cs"),
        # Api.Tests
        (templates_dir / "service" / "tests" / "Api.Tests" / "Api.Tests.csproj", target_dir / service_name / "tests" / "Api.Tests" / f"{service_name}.Api.Tests.csproj"),
        (templates_dir / "service" / "tests" / "Api.Tests" / "HealthEndpointTests.cs", target_dir / service_name / "tests" / "Api.Tests" / "HealthEndpointTests.cs"),
    ]

    missing_templates = [source for source, _ in template_files_map if not source.is_file()]
    if missing_templates:
        sys.stderr.write("Lỗi: Thiếu file template:\n")
        for source in missing_templates:
            sys.stderr.write(f"  {source}\n")
        sys.exit(1)

    print(f"Đang khởi tạo service '{service_name}' tại {target_dir}...")

    with tempfile.TemporaryDirectory(prefix=f".{service_name}-", dir=cwd) as staging:
        staging_dir = Path(staging)
        for rel_dir in TRACKED_DIRECTORIES_TEMPLATE:
            (staging_dir / rel_dir.format(service_name=service_name)).mkdir(parents=True, exist_ok=True)

        for source, destination in template_files_map:
            staged_file = staging_dir / destination.relative_to(target_dir)
            staged_file.parent.mkdir(parents=True, exist_ok=True)
            content = source.read_text(encoding="utf-8")
            for placeholder, value in replacements.items():
                content = content.replace(placeholder, value)
            staged_file.write_text(content, encoding="utf-8")

        finish_staging(staging_dir, service_name)
        if target_dir.exists() or target_dir.is_symlink():
            sys.stderr.write(f"Lỗi: Thư mục đích đã tồn tại: {target_dir}\n")
            sys.exit(1)
        staging_dir.rename(target_dir)

    print(f"Đã tạo thành công base service '{service_name}' tại: {target_dir}")
    print(f"Branch nền pre-push: origin/{short_name}/develop")
    print("\nCác bước tiếp theo:")
    print(f"  cd {service_name}")
    print("  cp .env.example .env; đặt POSTGRES_PASSWORD riêng và đổi POSTGRES_PORT nếu bận")
    print("  docker compose up -d")
    print("  Nạp ConnectionStrings__Postgres từ .env theo README.md")
    print(f"  dotnet build {service_name}.sln")
    print(f"  dotnet test {service_name}.sln")
    print("  (Tùy chọn) git init && git add . && git commit -m 'Initial commit'")
    print("  (Tùy chọn) ./scripts/setup-git-hooks.sh")


def finish_staging(target_dir: Path, service_name: str) -> None:
    """Thiết lập quyền thực thi và giữ đầy đủ cây thư mục mẫu trong Git."""
    executable_files = [
        target_dir / ".githooks" / "pre-push",
        target_dir / "scripts" / "pre-push-check.sh",
        target_dir / "scripts" / "setup-git-hooks.sh",
        target_dir / "scripts" / "tests" / "pre-push-check.test.sh",
    ]
    for exe_path in executable_files:
        if exe_path.exists():
            os.chmod(exe_path, 0o755)

    for rel_dir in TRACKED_DIRECTORIES_TEMPLATE:
        (target_dir / rel_dir.format(service_name=service_name) / ".gitkeep").touch()


if __name__ == "__main__":
    main()
