#!/usr/bin/env python3
"""Two real API processes + isolated PostgreSQL databases; no mocked business clients."""
import base64
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import urllib.parse
import uuid

ROOT = Path(__file__).resolve().parents[2]
PG_CONTAINER = os.environ.get("CONTRACT_TEST_PG_CONTAINER", "catalogservice-pg-test")
PG_PORT = os.environ.get("CONTRACT_TEST_PG_PORT", "5436")
PG_USER = os.environ.get("CONTRACT_TEST_PG_USER", "postgres")
PG_PASSWORD = os.environ.get("CONTRACT_TEST_PG_PASSWORD", "postgres")


def run(args, *, env=None, data=None):
    return subprocess.run(args, input=data, env=env, check=True, timeout=120, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def sql(database, statement):
    return run(["docker", "exec", "-i", "-e", "PGPASSWORD", PG_CONTAINER,
                "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", PG_USER, "-d", database],
               env={**os.environ, "PGPASSWORD": PG_PASSWORD}, data=statement.encode())


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def encode(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def request(base, path, expected=200, token=None, cookie=False, cookie_token=None):
    headers = {}
    if token:
        headers["Cookie" if cookie else "Authorization"] = f"accessToken={token}" if cookie else f"Bearer {token}"
    if cookie_token:
        headers["Cookie"] = f"accessToken={cookie_token}"
    req = urllib.request.Request(base + path, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    assert status == expected, f"{path}: expected {expected}, got {status}: {body[:400]!r}"
    return json.loads(body) if body[:1] in (b"{", b"[") else body.decode()


def wait_ready(base, process):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"API exited with code {process.returncode}")
        try:
            request(base, "/health/ready")
            return
        except (urllib.error.URLError, TimeoutError, AssertionError):
            time.sleep(0.2)
    raise TimeoutError(f"API not ready: {base}")


def main():
    suffix = uuid.uuid4().hex
    databases, processes, logs = [], [], []
    catalog_url, inventory_url = (f"http://127.0.0.1:{free_port()}" for _ in range(2))
    with tempfile.TemporaryDirectory(prefix="catalog-inventory-contract-") as temporary:
        work = Path(temporary)
        try:
            key = work / "test-key.pem"
            run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(key)])
            key.chmod(0o600)
            modulus = run(["openssl", "rsa", "-in", str(key), "-noout", "-modulus"]).decode().strip().split("=", 1)[1]
            jwks = {"keys": [{"kty": "RSA", "kid": "contract", "alg": "RS256", "use": "sig",
                              "n": encode(bytes.fromhex(modulus)), "e": "AQAB"}]}

            class KeysHandler(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    payload = json.dumps(jwks).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.end_headers()
                    self.wfile.write(payload)

                def log_message(self, *_):
                    pass

            server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), KeysHandler)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            keys_url = f"http://127.0.0.1:{server.server_port}/.well-known/jwks.json"

            def token(subject, role="cus", service=False, audience="dprental-clients"):
                now = int(time.time())
                payload = {"iss": "contract-tests", "aud": audience, "sub": subject,
                           "sid": str(uuid.uuid4()), "r": role, "iat": now, "nbf": now - 5, "exp": now + 600}
                if service:
                    payload["u"] = "svc"
                content = (encode(json.dumps({"alg": "RS256", "kid": "contract"}).encode()) + "." +
                           encode(json.dumps(payload).encode())).encode()
                signature = run(["openssl", "dgst", "-sha256", "-sign", str(key)], data=content)
                return content.decode() + "." + encode(signature)

            for service, base in [("CatalogService", catalog_url), ("InventoryService", inventory_url)]:
                db = f"contract_{service.lower()}_{suffix}"
                sql("postgres", f'CREATE DATABASE "{db}";')
                databases.append(db)
                connection = f"Host=127.0.0.1;Port={PG_PORT};Database={db};Username={PG_USER};Password={PG_PASSWORD}"
                project = ROOT / service / service / "src"
                env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": base,
                       "ConnectionStrings__Postgres": connection, "Logging__LogLevel__Default": "Warning",
                       "InventoryService__BaseUrl": inventory_url, "CatalogService__BaseUrl": catalog_url,
                       "MediaService__ApiKey": "contract-test-key", "Meilisearch__ApiKey": "",
                       "Kafka__AutoProvisionTopics": "false", "Kafka__ClientId": "contract-" + suffix,
                       "Auth__Jwt__Issuer": "contract-tests", "Auth__Jwt__Audience": "dprental-clients",
                       "Auth__Jwt__JwksUri": keys_url, "Authentication__Jwt__Issuer": "contract-tests",
                       "Authentication__Jwt__JwksUri": keys_url}
                run(["dotnet", "ef", "database", "update", "--no-build", "--project",
                     str(project / "Domain" / f"{service}.Domain.csproj"), "--startup-project",
                     str(project / "Api" / f"{service}.Api.csproj")], env=env)
                log = work / f"{service}.log"
                logs.append(log)
                with log.open("w") as output:
                    process = subprocess.Popen(["dotnet", str(project / "Api/bin/Debug/net10.0" / f"{service}.Api.dll")],
                                               cwd=project / "Api", env=env, stdout=output, stderr=subprocess.STDOUT)
                processes.append(process)
                wait_ready(base, process)

            gateway_url = f"http://127.0.0.1:{free_port()}"
            gateway_dir = ROOT / "Gateway/Gateway/ocelot"
            gateway_env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": gateway_url,
                           "Logging__LogLevel__Default": "Warning", "Gateway__Authentication__Jwt__Issuer": "contract-tests",
                           "Gateway__Authentication__Jwt__Audience": "dprental-clients",
                           "Gateway__Authentication__Jwt__InventoryAudience": "inventory-api",
                           "Gateway__Authentication__Jwt__JwksUri": keys_url}
            routes = json.loads((gateway_dir / "appsettings.json").read_text())["Routes"]
            for index, route in enumerate(routes):
                upstream = route["UpstreamPathTemplate"]
                target = catalog_url if "catalog" in upstream else inventory_url if "inventory" in upstream else keys_url
                target_port = urllib.parse.urlparse(target).port
                gateway_env.update({f"Routes__{index}__DownstreamScheme": "http",
                                    f"Routes__{index}__DownstreamHostAndPorts__0__Host": "127.0.0.1",
                                    f"Routes__{index}__DownstreamHostAndPorts__0__Port": str(target_port)})
            gateway_log = work / "Gateway.log"
            logs.append(gateway_log)
            with gateway_log.open("w") as output:
                gateway = subprocess.Popen(["dotnet", str(gateway_dir / "bin/Debug/net10.0/Gateway.Api.dll")],
                                           cwd=gateway_dir, env=gateway_env, stdout=output, stderr=subprocess.STDOUT)
            processes.append(gateway)
            wait_ready(gateway_url, gateway)

            category, brand, owner, other = (str(uuid.uuid4()) for _ in range(4))
            types = [str(uuid.uuid4()) for _ in range(101)]
            sql(databases[0], f'''INSERT INTO catalog."Categories" ("Id","Name","Slug","IsActive","CreatedAtUtc","UpdatedAtUtc")
                VALUES ('{category}','Contract category','contract-category',true,now(),now());
                INSERT INTO catalog."Brands" ("Id","Code","Name","Slug","IsActive","CreatedAt")
                VALUES ('{brand}','CONTRACT','Contract brand','contract-brand',true,now());''')
            sql(databases[0], "\n".join(
                f'''INSERT INTO catalog."EquipmentTypes" ("Id","Code","Name","NormalizedName","Status","CreatedAt","PublishedAt")
                VALUES ('{tid}','TYPE-{index:03}','Type {index:03}','TYPE {index:03}','Published',now(),now());
                INSERT INTO catalog."EquipmentTypeSpecSchemas" ("Id","EquipmentTypeId","Version","Definition","CreatedAt","PublishedAt")
                VALUES ('{uuid.uuid4()}','{tid}',1,'{{"fields":[]}}',now(),now());
                UPDATE catalog."EquipmentTypes" SET "CurrentSchemaVersion"=1 WHERE "Id"='{tid}';
                INSERT INTO catalog."EquipmentTypeCategories" ("EquipmentTypeId","CategoryId") VALUES ('{tid}','{category}');'''
                for index, tid in enumerate(types)))
            for subject, state, tid, units in [(owner, 2, types[-1], 3), (owner, 1, types[0], 1), (other, 2, types[-1], 1)]:
                listing = str(uuid.uuid4())
                sql(databases[1], f'''INSERT INTO inventory.equipment_listings
                    (id,owner_user_id,equipment_type_id,brand_id,name,model,manufacture_year,schema_version,spec_values,
                     daily_rate,currency,status,created_at,updated_at)
                    VALUES ('{listing}','{subject}','{tid}','{brand}','Contract listing','Contract model',2024,1,'{{}}',100000,'VND',{state},now(),now());''')
                sql(databases[1], "\n".join(
                    f'''INSERT INTO inventory.assets (id,equipment_listing_id,asset_code,serial_number,manufacture_year,accepting_bookings,created_at,updated_at)
                    VALUES ('{asset}','{listing}','AST-{asset.replace('-', '')}','SN-{asset}',2024,true,now(),now());'''
                    for asset in (str(uuid.uuid4()) for _ in range(units))))

            path = "/api/v1/catalog/"
            stats = request(inventory_url, "/api/v1/inventory/public/equipment-listings/statistics")
            assert stats["byEquipmentType"][types[-1]] == 2
            assert stats["byBrand"][brand] == 2
            public = request(catalog_url, path + "equipment-types")
            assert public["items"][0]["id"] == types[-1] and public["items"][0]["listingCount"] == 2
            assert request(catalog_url, path + "categories")[0]["listingCount"] == 2
            assert request(catalog_url, path + "brands")["items"][0]["listingCount"] == 2
            found = request(inventory_url, f"/api/v1/inventory/public/equipment-listings?categoryId={category}")
            assert found["totalCount"] == 2, found
            for role in ["cus", "adm"]:
                jwt = token(owner, role)
                for cookie in [False, True]:
                    mine = request(catalog_url, path + "equipment-types?scope=mine&listingStatus=Active", token=jwt, cookie=cookie)
                    assert mine["items"][0]["listingCount"] == 1
                    assert request(catalog_url, path + "categories?scope=mine", token=jwt, cookie=cookie)[0]["listingCount"] == 2
                    assert request(catalog_url, path + "brands?scope=mine", token=jwt, cookie=cookie)["items"][0]["listingCount"] == 2
            selected = request(catalog_url, path + "brands?scope=mine", token=token(owner), cookie_token=token(other))
            assert selected["items"][0]["listingCount"] == 2
            request(catalog_url, path + "brands?scope=mine", token=token(owner, service=True), expected=403)
            request(inventory_url, "/api/v1/inventory/equipment-listings/statistics",
                    token=token(owner, service=True, audience="inventory-api"), expected=403)
            request(inventory_url, "/api/v1/inventory/equipment-listings/statistics?unsupported=x", token=token(owner), expected=400)
            request(catalog_url, path + "equipment-types?scope=mine", expected=401)
            for query in ["minPrice=1", "scope=mine&status=Active", "scope=mine&scope=public"]:
                request(catalog_url, path + "equipment-types?" + query, expected=400)
            request(inventory_url, "/api/v1/inventory/public/equipment-listings/statistics?ownerUserId=x", expected=400)
            assert request(gateway_url, path + "brands")["items"][0]["listingCount"] == 2
            for cookie in [False, True]:
                result = request(gateway_url, path + "equipment-types?scope=mine&listingStatus=Active", token=token(owner), cookie=cookie)
                assert result["items"][0]["listingCount"] == 1
            assert request(gateway_url, "/api/v1/inventory/public/equipment-listings/statistics")["byBrand"][brand] == 2
            assert request(gateway_url, f"/api/v1/inventory/public/equipment-listings?categoryId={category}")["totalCount"] == 2
            processes[1].terminate()
            processes[1].wait(timeout=15)
            request(catalog_url, path + "equipment-types", expected=503)
            print("PASS: 101-type category paging, public/mine statistics, Bearer/cookie, owner/Admin isolation, validation and real downstream outage, and Gateway smoke")
        except BaseException:
            for log in logs:
                print(f"--- {log.name} (last lines) ---")
                print("\n".join(log.read_text().splitlines()[-15:]))
            raise
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
            for database in reversed(databases):
                sql("postgres", f'DROP DATABASE "{database}" WITH (FORCE);')
            if "server" in locals():
                server.shutdown()
                server.server_close()


if __name__ == "__main__":
    main()
