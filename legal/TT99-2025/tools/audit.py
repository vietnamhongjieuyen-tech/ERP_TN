"""Đối chiếu các mẫu BCTC của HAK với danh mục tài khoản Phụ lục II-A (TT 99/2025/TT-BTC).

Không cần văn bản gốc: kiểm những gì suy ra được từ danh mục tài khoản và chính các mẫu.
  1. Mọi số hiệu tài khoản trong công thức đều có trong Phụ lục II-A (bắt lỗi gõ nhầm, TK của chế độ cũ).
  2. Mọi TK chi tiết loại 1–4 (tài sản, nợ phải trả, vốn chủ sở hữu) được ít nhất một chỉ tiêu B01-DN lấy số dư.
  3. Mọi TK chi tiết loại 5–8 (doanh thu, chi phí, thu nhập khác, chi phí khác) được ít nhất một chỉ tiêu B02-DN lấy phát sinh,
     trừ TK chi phí sản xuất (621, 622, 623, 627) và 154 — kết chuyển vào giá thành / giá vốn, B02 lấy qua 632.
  4. Mã số chỉ tiêu không trùng; LINE(x) chỉ tham chiếu Mã số có trong cùng mẫu.
  5. Xuất bảng kiểm từng chỉ tiêu (CSV, mở bằng Excel) để kế toán trưởng ghi kết quả đối chiếu với văn bản gốc.

Dùng: python -I audit.py <thư mục legal/TT99-2025>
"""
import csv, json, re, sys
from pathlib import Path

root = Path(sys.argv[1])
sys.stdout.reconfigure(encoding="utf-8")

chart = []
with open(root / "phu-luc-II-A-danh-muc-tai-khoan.csv", encoding="utf-8-sig") as f:
    for r in csv.DictReader(f, delimiter=";"):
        chart.append({"code": r["Số hiệu TK"], "name": r["Tên tài khoản"], "detail": r["Chi tiết"] == "Có", "type": r["Loại tài khoản"]})
codes = {a["code"] for a in chart}
details = [a for a in chart if a["detail"]]

def loai(a):  # 1..9 from the first digit (Phụ lục II-A groups accounts by their first digit)
    return int(a["code"][0])

ACC = re.compile(r"(?<![\w.])(\d{3,6})(\*?)(?:\[[^\]]*\])?")
SKIP = re.compile(r"\b(LINE|CF|ROUND|MAX0|ABS)\(([^()]*)\)")

def patterns(formula):
    """Account patterns in a formula, without the arguments of LINE() / CF() (those are Mã số, not accounts)."""
    text = SKIP.sub(lambda m: m.group(1) + "()", formula or "")
    return [(m.group(1), m.group(2) == "*") for m in ACC.finditer(text)]

def matches(pattern, star, code):
    return code.startswith(pattern) if star else code == pattern

problems, checklist, used = [], [], {}
for path in sorted(root.glob("mau-*.json")):
    t = json.loads(path.read_text(encoding="utf-8"))
    lines = t["lines"]
    seen = set()
    for ln in lines:
        if ln["code"] in seen:
            problems.append(f"{path.name}: Mã số {ln['code']} bị trùng")
        seen.add(ln["code"])
    for ln in lines:
        for part in (ln.get("formula") or "").split("|"):
            for ref in re.findall(r"LINE\(([^)]+)\)", part):
                if ref.strip() not in seen:
                    problems.append(f"{path.name} Mã {ln['code']}: LINE({ref}) không có trong mẫu")
            for pat, star in patterns(part):
                if not any(matches(pat, star, c) for c in codes):
                    problems.append(f"{path.name} Mã {ln['code']}: tài khoản {pat}{'*' if star else ''} không có trong Phụ lục II-A")
                used.setdefault(t["code"] if "code" in t else path.stem, []).append((pat, star, ln["code"]))
        checklist.append({
            "Mẫu": path.stem.replace("mau-", ""), "Mã số": ln["code"], "Chỉ tiêu (tên trong mẫu HAK)": ln["name"],
            "Công thức HAK": ln.get("formula") or "", "Diễn giải": ln.get("note") or "",
            "Căn cứ": t.get("legalReference", ""), "Tên khớp văn bản gốc (Đ/S)": "", "Công thức khớp hướng dẫn lập (Đ/S)": "",
            "Người đối chiếu": "", "Ghi chú": "",
        })

def covered(template_stem, acc):
    t = json.loads((root / f"{template_stem}.json").read_text(encoding="utf-8"))
    for ln in t["lines"]:
        for pat, star in patterns(ln.get("formula")):
            if matches(pat, star, acc["code"]):
                return ln["code"]
    return None

print("# Kết quả tự kiểm tra mẫu BCTC theo danh mục TK Phụ lục II-A\n")
print(f"- Danh mục: {len(chart)} tài khoản, {len(details)} TK chi tiết.")
print(f"- Mẫu kiểm: {', '.join(p.stem for p in sorted(root.glob('mau-*.json')))}.\n")

print("## 1. Tài khoản trong công thức và tham chiếu Mã số\n")
print("\n".join(f"- {p}" for p in problems) if problems else "- Không có lỗi: mọi tài khoản trong công thức đều có trong Phụ lục II-A, mọi LINE() trỏ đúng Mã số, không trùng Mã số.")

b01_missing = [a for a in details if loai(a) <= 4 and not covered("mau-B01-DN", a)]
print("\n## 2. B01-DN: TK chi tiết loại 1–4 chưa chỉ tiêu nào lấy số dư\n")
print("\n".join(f"- {a['code']} {a['name']}" for a in b01_missing) if b01_missing else "- Không có: mọi TK chi tiết loại 1–4 đều vào ít nhất một chỉ tiêu B01-DN.")

production = ("621", "622", "623", "627", "154")
b02_missing = [a for a in details if 5 <= loai(a) <= 8 and not a["code"].startswith(production) and not covered("mau-B02-DN", a)]
print("\n## 3. B02-DN: TK chi tiết loại 5–8 chưa chỉ tiêu nào lấy phát sinh\n")
print("\n".join(f"- {a['code']} {a['name']}" for a in b02_missing) if b02_missing else "- Không có: mọi TK doanh thu, chi phí, thu nhập khác, chi phí khác (trừ chi phí sản xuất 621–627 kết chuyển qua giá thành) đều vào B02-DN.")

out = root / "ho-so-doi-chieu-chi-tieu.csv"
with open(out, "w", encoding="utf-8-sig", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(checklist[0].keys()), delimiter=";")
    w.writeheader()
    w.writerows(checklist)
print(f"\n## 4. Bảng kiểm từng chỉ tiêu\n\n- {len(checklist)} dòng → `{out.name}` (mở bằng Excel; cột Đ/S để kế toán trưởng ghi kết quả đối chiếu với văn bản gốc).")
sys.exit(1 if problems or b01_missing or b02_missing else 0)
