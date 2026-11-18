"""So tài khoản trong công thức mẫu HAK với tài khoản mà hướng dẫn lập BCTC (TT 99/2025, Công báo 1579–1582) nêu cho từng Mã số.

Đầu vào: toàn văn hướng dẫn (word-text.ps1). Với mỗi đoạn "(Mã số X)" lấy các số hiệu đứng sau "Tài khoản"/"TK"; so với tài khoản
trong công thức (bỏ các mẫu loại trừ "!…"). Một TK của hướng dẫn được coi là có trong công thức khi TK đó và một mẫu công thức là
tiền tố của nhau (công thức "128*" bao TK 1281, 1288). Kết quả là danh sách cần người đọc xem lại, không phải kết luận: hướng dẫn
cũng nêu TK để loại trừ ("không bao gồm …") hoặc TK đối ứng.

Dùng: python -I verify-formulas.py <huongdan-1579.txt> <huongdan-1581.txt> <thư mục legal/TT99-2025> [<bảng CSV ra>]
"""
import csv, json, re, sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
g1579, g1581, root = Path(sys.argv[1]).read_text(encoding="utf-8"), Path(sys.argv[2]).read_text(encoding="utf-8"), Path(sys.argv[3])
out = Path(sys.argv[4]) if len(sys.argv) > 4 else None

HEAD = re.compile(r"\(Mã số\s+([0-9]{2,3})\s*([a-dA-D])?\s*\)")
ACC = re.compile(r"(?:Tài khoản|tài khoản|TK)\s+(\d{3,5}(?:\s*(?:,|;|và|hoặc)\s*\d{3,5})*)")

def segments(text, start, end):
    heads = [m for m in HEAD.finditer(text, start, end)]
    res = {}
    for i, m in enumerate(heads):
        code = m.group(1) + (m.group(2) or "").upper()
        stop = heads[i + 1].start() if i + 1 < len(heads) else end
        body = text[m.end():stop]
        accs = set()
        for a in ACC.finditer(body):
            accs.update(re.findall(r"\d{3,5}", a.group(1)))
        res.setdefault(code, set()).update(accs)
    return res

def formula_accounts(formula):
    text = re.sub(r"\b(LINE|CF)\([^()]*\)", "", formula or "")
    inc = set(re.findall(r"(?<![\w!])(\d{3,6})\*?", text))
    exc = set(re.findall(r"!(\d{3,6})\*?", text))
    return inc - exc

def related(a, b):
    return a.startswith(b) or b.startswith(a)

b02_from = g1579.find("(Mã số 01)")
b03_ind = g1581.find("Lợi nhuận trước thuế (Mã số 01)")
b03_inv = g1581.find("(Mã số 21)", b03_ind)
plans = [
    ("mau-B01-DN", segments(g1579, 0, b02_from), "Công báo 1579+1580, Phần B mục 1.4"),
    ("mau-B02-DN", {**segments(g1579, b02_from, len(g1579)), **segments(g1581, 0, g1581.find("4.1.1"))}, "Công báo 1579–1582, Phần C mục 3"),
    ("mau-B03-DN-gian-tiep", segments(g1581, b03_ind, b03_inv), "Công báo 1581+1582, Phần D mục 4.1.2"),
]
rows, flagged = [], 0
for stem, guide, ref in plans:
    t = json.loads((root / f"{stem}.json").read_text(encoding="utf-8"))
    for ln in t["lines"]:
        code = ln["code"].upper()
        f = formula_accounts(ln.get("formula"))
        g = guide.get(code, set())
        if not f and not g:
            continue
        missing = sorted(x for x in g if not any(related(x, y) for y in f))
        extra = sorted(y for y in f if not any(related(x, y) for x in g))
        status = "khớp" if not missing and not extra else "xem lại"
        if status != "khớp": flagged += 1
        rows.append({"Mẫu": stem.replace("mau-", ""), "Mã số": ln["code"], "Chỉ tiêu": ln["name"], "TK theo hướng dẫn": " ".join(sorted(g)),
                     "TK trong công thức": " ".join(sorted(f)), "Hướng dẫn có, công thức không": " ".join(missing),
                     "Công thức có, hướng dẫn không nêu": " ".join(extra), "Kết quả máy": status, "Căn cứ": ref, "Kết luận người đọc": ""})
print(f"{len(rows)} chỉ tiêu có tài khoản; khớp hoàn toàn {len(rows) - flagged}; cần đọc lại {flagged}")
for r in rows:
    if r["Kết quả máy"] != "khớp":
        print(f"- {r['Mẫu']} {r['Mã số']} {r['Chỉ tiêu'][:60]}: thiếu [{r['Hướng dẫn có, công thức không']}] thừa [{r['Công thức có, hướng dẫn không nêu']}]")
if out:
    with open(out, "w", encoding="utf-8-sig", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()), delimiter=";")
        w.writeheader(); w.writerows(rows)
