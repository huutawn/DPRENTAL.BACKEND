#!/usr/bin/env python3
"""
Công cụ kiểm tra tài liệu và fixtures của ReviewService.
Kiểm tra:
1. Tính hợp lệ của toàn bộ JSON fixtures và đối chiếu manifest.
2. Tính toàn vẹn của liên kết tương đối (relative links) trong tất cả file Markdown.
3. Kiểm tra lỗi khoảng trắng (trailing whitespace, CRLF, EOF newline) trên các file tài liệu.
4. Kiểm tra tính nhất quán giữa các tài liệu contract (tên trường, enum, status literals, topic).
"""

import os
import re
import sys
import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DOCS_DIR = REPO_ROOT / "docs"
FIXTURES_DIR = DOCS_DIR / "fixtures"

errors = []
warnings = []

def log_error(msg: str):
    errors.append(msg)
    print(f"❌ [ERROR] {msg}", file=sys.stderr)

def log_warn(msg: str):
    warnings.append(msg)
    print(f"⚠️  [WARN]  {msg}")

def log_info(msg: str):
    print(f"ℹ️  [INFO]  {msg}")

# ============================================================================
# 1. KIỂM TRA JSON FIXTURES VÀ MANIFEST
# ============================================================================
def check_fixtures():
    log_info("1. Kiểm tra JSON fixtures và manifest...")
    manifest_path = FIXTURES_DIR / "manifest.json"
    if not manifest_path.exists():
        log_error(f"Không tìm thấy file manifest tại {manifest_path}")
        return

    with open(manifest_path, "r", encoding="utf-8") as f:
        try:
            manifest = json.load(f)
        except Exception as e:
            log_error(f"Lỗi parse JSON trong manifest.json: {e}")
            return

    # Duyệt toàn bộ file trong docs/fixtures
    all_fixture_files = list(FIXTURES_DIR.glob("**/*.json"))
    manifest_files_set = set()

    for category, cat_data in manifest.get("categories", {}).items():
        for item in cat_data.get("fixtures", []):
            rel_file = item.get("file")
            if not rel_file:
                log_error(f"Mục trong category '{category}' thiếu trường 'file': {item}")
                continue
            full_path = FIXTURES_DIR / rel_file
            manifest_files_set.add(full_path.resolve())
            if not full_path.exists():
                log_error(f"Manifest trỏ tới file không tồn tại: {rel_file}")
            else:
                # Kiểm tra nội dung file
                if "invalid_syntax" in category or "invalid-syntax" in rel_file:
                    with open(full_path, "r", encoding="utf-8") as jf:
                        content = jf.read()
                        try:
                            json.loads(content)
                            log_error(f"File {rel_file} được đánh dấu là invalid-syntax nhưng lại parse JSON thành công!")
                        except json.JSONDecodeError:
                            log_info(f"  [OK] {rel_file} là invalid JSON như mong đợi.")
                else:
                    with open(full_path, "r", encoding="utf-8") as jf:
                        try:
                            json.load(jf)
                            log_info(f"  [OK] {rel_file} parse JSON thành công.")
                        except Exception as e:
                            log_error(f"Lỗi parse JSON tại file hợp lệ {rel_file}: {e}")

    # Kiểm tra xem có file json nào nằm ngoài manifest không
    for f in all_fixture_files:
        if f.resolve() == manifest_path.resolve():
            continue
        if f.resolve() not in manifest_files_set:
            log_warn(f"File fixture chưa được đăng ký trong manifest: {f.relative_to(FIXTURES_DIR)}")

# ============================================================================
# 2. KIỂM TRA LIÊN KẾT TƯƠNG ĐỐI TRONG CÁC FILE MARKDOWN
# ============================================================================
def check_markdown_links():
    log_info("2. Kiểm tra liên kết tương đối trong các file Markdown...")
    md_files = list(DOCS_DIR.glob("**/*.md")) + [REPO_ROOT / "README.md", REPO_ROOT / "AGENTS.md"]
    link_pattern = re.compile(r'\[([^\]]+)\]\(([^)]+)\)')

    for md_file in md_files:
        if not md_file.exists():
            continue
        with open(md_file, "r", encoding="utf-8") as f:
            content = f.read()

        for match in link_pattern.finditer(content):
            text, target = match.groups()
            target = target.strip()

            # Bỏ qua liên kết bên ngoài hoặc anchor nội bộ thuần túy
            if target.startswith("http://") or target.startswith("https://") or target.startswith("mailto:"):
                continue
            if target.startswith("#"):
                continue

            # Tách anchor nếu có (ví dụ: path/to/file.md#anchor)
            target_path_part = target.split("#")[0]
            if not target_path_part:
                continue

            resolved_path = (md_file.parent / target_path_part).resolve()
            if not resolved_path.exists():
                log_error(f"Liên kết hỏng tại {md_file.relative_to(REPO_ROOT)}: trỏ tới '{target}' (không tìm thấy {resolved_path})")
            else:
                pass

# ============================================================================
# 3. KIỂM TRA WHITESPACE VÀ ĐỊNH DẠNG FILE
# ============================================================================
def check_whitespace_and_formatting():
    log_info("3. Kiểm tra whitespace, CRLF và newline cuối file...")
    target_extensions = {".md", ".json", ".sh", ".py"}

    files_to_check = []
    for ext in target_extensions:
        files_to_check.extend(DOCS_DIR.glob(f"**/*{ext}"))

    for file_path in files_to_check:
        try:
            with open(file_path, "rb") as f:
                raw_bytes = f.read()
        except Exception as e:
            log_error(f"Không thể đọc file {file_path}: {e}")
            continue

        if b"\r\n" in raw_bytes:
            log_error(f"File chứa ký tự CRLF (Windows line endings): {file_path.relative_to(REPO_ROOT)}")

        if raw_bytes and not raw_bytes.endswith(b"\n"):
            log_error(f"File thiếu ký tự xuống dòng (newline) ở cuối file: {file_path.relative_to(REPO_ROOT)}")

        # Kiểm tra trailing whitespace từng dòng
        text_lines = raw_bytes.decode("utf-8", errors="replace").split("\n")
        for line_no, line in enumerate(text_lines, start=1):
            if line.endswith(" ") or line.endswith("\t"):
                log_error(f"Trailing whitespace tại dòng {line_no} trong {file_path.relative_to(REPO_ROOT)}")

# ============================================================================
# 4. KIỂM TRA TÍNH NHẤT QUÁN CHÉO (CROSS-REFERENCE CONSISTENCY)
# ============================================================================
def check_cross_consistency():
    log_info("4. Kiểm tra tính nhất quán giữa các tài liệu contract...")

    arch = (DOCS_DIR / "ARCHITECTURE.md").read_text(encoding="utf-8")
    api = (DOCS_DIR / "contracts/review-api-v1.md").read_text(encoding="utf-8")
    elig = (DOCS_DIR / "contracts/review-eligibility-v1.md").read_text(encoding="utf-8")
    events = (DOCS_DIR / "contracts/review-events-v1.md").read_text(encoding="utf-8")
    vis = (DOCS_DIR / "contracts/review-visibility-v1.md").read_text(encoding="utf-8")
    erd = (DOCS_DIR / "erd.md").read_text(encoding="utf-8")

    # 1. Trục trạng thái hai trục
    for doc_name, text in [("API", api), ("Visibility", vis), ("ERD", erd), ("Architecture", arch)]:
        if "authorState" not in text and "author_state" not in text:
            log_error(f"Tài liệu {doc_name} thiếu khai báo authorState / author_state")
        if "moderationState" not in text and "moderation_state" not in text:
            log_error(f"Tài liệu {doc_name} thiếu khai báo moderationState / moderation_state")

    # 2. Topic name
    if "rental.events.v1" not in events:
        log_error("review-events-v1.md không chứa topic chính xác rental.events.v1")
    if "rental.events.v1" not in arch:
        log_error("ARCHITECTURE.md không chứa topic chính xác rental.events.v1")

    # 3. Strong ETag và Concurrency Token
    if "If-Match" not in api or "428" not in api or "412" not in api:
        log_error("review-api-v1.md thiếu đặc tả If-Match hoặc mã 428/412")
    if "version BIGINT" not in erd and "version" not in erd:
        log_error("erd.md thiếu cột version BIGINT")

    # 4. Status literals RentalItemStatus
    for literal in ["PendingPayment", "PendingSupplierConfirmation", "AwaitingHandover", "InRental", "PendingSettlement", "Completed", "Rejected", "Expired", "Cancelled"]:
        if literal not in elig:
            log_error(f"review-eligibility-v1.md thiếu status literal: {literal}")

    log_info("  [OK] Đã hoàn tất kiểm tra tính nhất quán chéo.")

def main():
    print("=== BẮT ĐẦU KIỂM TRA TÀI LIỆU REVIEWSERVICE ===")
    check_fixtures()
    check_markdown_links()
    check_whitespace_and_formatting()
    check_cross_consistency()

    print("\n=== KẾT QUẢ KIỂM TRA ===")
    print(f"Tổng số cảnh báo: {len(warnings)}")
    print(f"Tổng số lỗi: {len(errors)}")

    if errors:
        print("\n❌ CÓ LỖI CẦN SỬA!", file=sys.stderr)
        sys.exit(1)
    else:
        print("\n✅ TOÀN BỘ KIỂM TRA TÀI LIỆU VÀ FIXTURES ĐÃ ĐẠT!")
        sys.exit(0)

if __name__ == "__main__":
    main()
