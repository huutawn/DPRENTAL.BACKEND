"""Regression tests for the standalone service generator."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "create-service.py"


class CreateServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.working_directory = Path(self.temporary_directory.name)

    def generate(self, name: str, script: Path = SCRIPT) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(script), name],
            cwd=self.working_directory,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_generates_in_calling_directory_with_expected_tree(self) -> None:
        result = self.generate("CatalogService")

        self.assertEqual(0, result.returncode, result.stderr)
        project = self.working_directory / "CatalogService"
        self.assertTrue((project / "CatalogService.sln").is_file())
        self.assertTrue((project / "CatalogService/src/Domain/Repositories/CatalogServiceDbContext.cs").is_file())
        self.assertEqual(43, len(list(project.rglob(".gitkeep"))))
        for folder in (
            "CatalogService/src/Api/Clients",
            "CatalogService/src/Application/Clients",
            "CatalogService/src/Application/IServices",
            "CatalogService/src/Application/Services",
            "CatalogService/src/Domain/IRepositories",
            "CatalogService/src/Domain/Repositories",
            "CatalogService/src/Application/DTOs",
        ):
            self.assertTrue((project / folder / ".gitkeep").is_file(), folder)
        self.assertFalse((project / ".git").exists())
        self.assertIn("Microsoft.EntityFrameworkCore", (project / "CatalogService/src/Domain/CatalogService.Domain.csproj").read_text())

    def test_two_services_have_distinct_ports_and_no_default_password(self) -> None:
        for name in ("CatalogService", "OrderService"):
            result = self.generate(name)
            self.assertEqual(0, result.returncode, result.stderr)

        catalog = self.working_directory / "CatalogService"
        order = self.working_directory / "OrderService"
        catalog_env = (catalog / ".env.example").read_text()
        order_env = (order / ".env.example").read_text()
        self.assertIn("POSTGRES_PASSWORD=\n", catalog_env)
        catalog_postgres_port = next(line for line in catalog_env.splitlines() if line.startswith("POSTGRES_PORT="))
        order_postgres_port = next(line for line in order_env.splitlines() if line.startswith("POSTGRES_PORT="))
        self.assertNotEqual(catalog_postgres_port, order_postgres_port)
        self.assertNotIn("Password=", (catalog / "CatalogService/src/Api/appsettings.Development.json").read_text())
        catalog_launch = json.loads((catalog / "CatalogService/src/Api/Properties/launchSettings.json").read_text())
        order_launch = json.loads((order / "OrderService/src/Api/Properties/launchSettings.json").read_text())
        self.assertNotEqual(
            catalog_launch["profiles"]["CatalogService.Api"]["applicationUrl"],
            order_launch["profiles"]["OrderService.Api"]["applicationUrl"],
        )

    def test_existing_target_and_invalid_name_do_not_write(self) -> None:
        target = self.working_directory / "CatalogService"
        target.mkdir()
        marker = target / "keep.txt"
        marker.write_text("unchanged")

        self.assertNotEqual(0, self.generate("CatalogService").returncode)
        self.assertEqual("unchanged", marker.read_text())
        self.assertNotEqual(0, self.generate("../BadService").returncode)
        self.assertEqual([target], list(self.working_directory.iterdir()))

    def test_missing_template_fails_without_partial_project(self) -> None:
        copied_script = self.working_directory / "generator" / "create-service.py"
        copied_script.parent.mkdir()
        shutil.copy2(SCRIPT, copied_script)
        shutil.copytree(SCRIPT.parent / "templates", copied_script.parent / "templates")
        (copied_script.parent / "templates/root/Dockerfile").unlink()

        result = self.generate("CatalogService", copied_script)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Thiếu file template", result.stderr)
        self.assertFalse((self.working_directory / "CatalogService").exists())


if __name__ == "__main__":
    unittest.main()
