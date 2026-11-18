"""Builds the B01-DN / B02-DN report templates (HAK report-engine JSON) from the Word tables of TT 99/2025 Phụ lục IV.
Names come from the official table cells; formulas follow the preparation guide (Công báo 1579+1580, Phần B and C)."""
import json, sys, os

tables, out = sys.argv[1], sys.argv[2]

def read(name):
    rows = {}
    for line in open(os.path.join(tables, name), encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 3:
            rows.setdefault(int(p[0]), {})[int(p[1])] = p[2].strip()
    return [rows[k] for k in sorted(rows)]

def names(rows, code_col):
    """code -> name from the official table; merged rows (411/420 with a, b) are split on ' | '."""
    result, order = {}, []
    for r in rows[2:]:
        code, name = r.get(code_col, ""), r.get(1, "")
        if not code and not name: continue
        codes, labels = [c.strip() for c in code.split("|")], [n.strip() for n in name.split("|")]
        if len(codes) > 1:
            for c, n in zip(codes, labels): result[c.upper()] = n; order.append(c.upper())
        elif code:
            result[code.upper()] = name.replace(" | ", " "); order.append(code.upper())
        else:
            order.append(("HEAD", name))
    return result, order

# ---------------------------------------------------------------- B01-DN
B01 = {
 "100": ("LINE(110) + LINE(120) + LINE(130) + LINE(140) + LINE(150) + LINE(160)", "B.1.4.a"),
 "110": ("LINE(111) + LINE(112)", ""),
 "111": ("BAL_D(111*[!HCSD], 112*[!HCSD], 113*[!HCSD])", "Dư Nợ 111, 112, 113 không bị hạn chế sử dụng"),
 "112": ("BAL_D(1281*[TDT,!HCSD], 1288*[TDT,!HCSD])", "Dư Nợ 1281, 1288 chi tiết khoản tương đương tiền (nhãn TDT)"),
 "120": ("LINE(121) + LINE(122) + LINE(123) + LINE(124) + LINE(125) + LINE(126)", ""),
 "121": ("BAL_D(121*)", "Dư Nợ 121"),
 "122": ("-BAL_C(2291*)", "Dư Có 2291, ghi âm"),
 "123": ("BAL_D(128*[!TDT,!DH])", "Dư Nợ 1281–1288 kỳ hạn còn lại ≤ 12 tháng, không phải tương đương tiền"),
 "124": ("-BAL_C(2292*[HTM,!DH])", "Dư Có 2292 chi tiết dự phòng đầu tư nắm giữ đến ngày đáo hạn ngắn hạn (nhãn HTM), ghi âm"),
 "125": ("BAL_D(2281*[BCC,!DH])", "Dư Nợ 2281 chi tiết hợp đồng BCC ≤ 12 tháng (nhãn BCC)"),
 "126": ("-BAL_C(2292*[BCC,!DH])", "Dư Có 2292 chi tiết dự phòng BCC ngắn hạn, ghi âm"),
 "130": ("LINE(131) + LINE(132) + LINE(133) + LINE(134) + LINE(135) + LINE(136) + LINE(137)", ""),
 "131": ("GROSS_D(131*[!DH])", "Tổng dư Nợ chi tiết 131 theo từng khách hàng"),
 "132": ("GROSS_D(331*[!DH])", "Tổng dư Nợ chi tiết 331 theo từng người bán"),
 "133": ("GROSS_D(1362*[!DH], 1363*[!DH], 1368*[!DH])", "Dư Nợ chi tiết 1362, 1363, 1368"),
 "134": ("GROSS_D(337*)", "Dư Nợ chi tiết 337"),
 "135": ("GROSS_D(1388*[!DH], 334*[!DH], 338*[!DH], 141*[!DH], 244*[!DH])", "Dư Nợ chi tiết 1388, 334, 338, 141, 244"),
 "136": ("-BAL_C(2293*[!DH])", "Dư Có 2293 chi tiết dự phòng ngắn hạn, ghi âm"),
 "137": ("BAL_D(1381*)", "Dư Nợ 1381"),
 "140": ("LINE(141) + LINE(142)", ""),
 "141": ("BAL_D(151*, 152*, 153*[!DH], 154*[!DH], 155*, 156*, 157*, 158*)", "Dư Nợ 151–158 (trừ 154, 153 dài hạn — nhãn DH)"),
 "142": ("-BAL_C(2294*[!DH])", "Dư Có 2294 chi tiết cho hàng tồn kho Mã 141, ghi âm"),
 "150": ("LINE(151) + LINE(152) + LINE(153)", ""),
 "151": ("BAL_D(2152*[!DH])", "Dư Nợ chi tiết 2152 ngắn hạn"),
 "152": ("BAL_D(2153*[!DH])", "Dư Nợ chi tiết 2153 ngắn hạn"),
 "153": ("-BAL_C(2295*[!DH])", "Dư Có chi tiết 2295 ngắn hạn, ghi âm"),
 "160": ("LINE(161) + LINE(162) + LINE(163) + LINE(164) + LINE(165)", ""),
 "161": ("BAL_D(242*[!DH])", "Dư Nợ chi tiết 242 phân bổ ≤ 12 tháng"),
 "162": ("BAL_D(133*)", "Dư Nợ 133"),
 "163": ("GROSS_D(1383*, 333*)", "Dư Nợ chi tiết 1383, 333"),
 "164": ("BAL_D(171*)", "Dư Nợ 171"),
 "165": ("BAL_D(2288*[!DH]) + BAL_D(111*[HCSD,!DH], 112*[HCSD,!DH], 113*[HCSD,!DH], 128*[TDT,HCSD,!DH])", "Dư Nợ chi tiết 2288 + tiền, tương đương tiền bị hạn chế sử dụng ngắn hạn (nhãn HCSD)"),
 "200": ("LINE(210) + LINE(220) + LINE(230) + LINE(240) + LINE(250) + LINE(260) + LINE(270)", "B.1.4.b"),
 "210": ("LINE(211) + LINE(212) + LINE(213) + LINE(214) + LINE(215) + LINE(216)", ""),
 "211": ("GROSS_D(131*[DH])", "Dư Nợ chi tiết 131 dài hạn"),
 "212": ("GROSS_D(331*[DH])", "Dư Nợ chi tiết 331 dài hạn"),
 "213": ("BAL_D(1361*)", "Dư Nợ 1361"),
 "214": ("GROSS_D(1362*[DH], 1363*[DH], 1368*[DH])", "Dư Nợ chi tiết 1362, 1363, 1368 dài hạn"),
 "215": ("GROSS_D(1388*[DH], 334*[DH], 338*[DH], 141*[DH], 244*[DH])", "Dư Nợ chi tiết 1388, 334, 338, 141, 244 dài hạn"),
 "216": ("-BAL_C(2293*[DH])", "Dư Có 2293 chi tiết dài hạn, ghi âm"),
 "220": ("LINE(221) + LINE(224) + LINE(227)", ""),
 "221": ("LINE(222) + LINE(223)", ""), "222": ("BAL_D(211*)", "Dư Nợ 211"), "223": ("-BAL_C(2141*)", "Dư Có 2141, ghi âm"),
 "224": ("LINE(225) + LINE(226)", ""), "225": ("BAL_D(212*)", "Dư Nợ 212"), "226": ("-BAL_C(2142*)", "Dư Có 2142, ghi âm"),
 "227": ("LINE(228) + LINE(229)", ""), "228": ("BAL_D(213*)", "Dư Nợ 213"), "229": ("-BAL_C(2143*)", "Dư Có 2143, ghi âm"),
 "230": ("LINE(231) + LINE(236) + LINE(237) + LINE(238)", ""),
 "231": ("LINE(232) + LINE(233)", ""), "232": ("BAL_D(21511*)", "Dư Nợ 21511"),
 "233": ("LINE(234) + LINE(235)", ""), "234": ("BAL_D(215121*)", "Dư Nợ 215121"), "235": ("-BAL_C(215122*)", "Dư Có 215122, ghi âm"),
 "236": ("BAL_D(2152*[DH])", "Dư Nợ chi tiết 2152 dài hạn"), "237": ("BAL_D(2153*[DH])", "Dư Nợ chi tiết 2153 dài hạn"),
 "238": ("-BAL_C(2295*[DH])", "Dư Có chi tiết 2295 dài hạn, ghi âm"),
 "240": ("LINE(241) + LINE(242)", ""), "241": ("BAL_D(217*)", "Dư Nợ 217"), "242": ("-BAL_C(2147*)", "Dư Có 2147, ghi âm"),
 "250": ("LINE(251) + LINE(252)", ""),
 "251": ("BAL_D(154*[DH]) - BAL_C(2294*[DH,SXDD])", "Dư Nợ 154 dài hạn − dự phòng tương ứng (2294 nhãn DH,SXDD)"),
 "252": ("BAL_D(241*)", "Dư Nợ 241"),
 "260": ("LINE(261) + LINE(262) + LINE(263) + LINE(264) + LINE(265) + LINE(266)", ""),
 "261": ("BAL_D(221*)", "Dư Nợ 221"), "262": ("BAL_D(222*)", "Dư Nợ 222"),
 "263": ("BAL_D(2281*) - LINE(125)", "Dư Nợ 2281 trừ phần BCC ngắn hạn đã ở Mã 125"),
 "264": ("-BAL_C(2292*[!HTM]) + BAL_C(2292*[BCC,!DH])", "Dư Có 2292 (trừ dự phòng HTM và BCC ngắn hạn), ghi âm"),
 "265": ("BAL_D(128*[DH,!TDT])", "Dư Nợ 1281–1288 kỳ hạn còn lại > 12 tháng"),
 "266": ("-BAL_C(2292*[HTM,DH])", "Dư Có 2292 chi tiết dự phòng HTM dài hạn, ghi âm"),
 "270": ("LINE(271) + LINE(272) + LINE(273) + LINE(274)", ""),
 "271": ("BAL_D(242*[DH])", "Dư Nợ chi tiết 242 dài hạn"), "272": ("BAL_D(243*)", "Dư Nợ 243"),
 "273": ("BAL_D(153*[DH]) - BAL_C(2294*[DH,!SXDD])", "Dư Nợ 153 phụ tùng dự trữ dài hạn − dự phòng tương ứng"),
 "274": ("BAL_D(2288*[DH]) + BAL_D(111*[HCSD,DH], 112*[HCSD,DH], 113*[HCSD,DH])", "Dư Nợ 2288 dài hạn + tiền bị hạn chế sử dụng dài hạn"),
 "280": ("LINE(100) + LINE(200)", "B.1.4.c"),
 "300": ("LINE(310) + LINE(330)", "B.1.4.d"),
 "310": ("LINE(311) + LINE(312) + LINE(313) + LINE(314) + LINE(315) + LINE(316) + LINE(317) + LINE(318) + LINE(319) + LINE(320) + LINE(321) + LINE(322) + LINE(323) + LINE(324) + LINE(325)", ""),
 "311": ("GROSS_C(331*[!DH])", "Dư Có chi tiết 331 theo từng người bán"),
 "312": ("GROSS_C(131*[!DH])", "Dư Có chi tiết 131 theo từng khách hàng"),
 "313": ("BAL_C(332*)", "Dư Có 332"),
 "314": ("GROSS_C(333*[!DH])", "Dư Có chi tiết 333"),
 "315": ("GROSS_C(334*)", "Dư Có chi tiết 334"),
 "316": ("BAL_C(335*[!DH])", "Dư Có chi tiết 335 ngắn hạn"),
 "317": ("GROSS_C(3362*[!DH], 3363*[!DH], 3368*[!DH])", "Dư Có chi tiết 3362, 3363, 3368"),
 "318": ("GROSS_C(337*)", "Dư Có chi tiết 337"),
 "319": ("BAL_C(3387*[!DH])", "Dư Có chi tiết 3387 ngắn hạn"),
 "320": ("GROSS_C(338*[!DH], !3387*, 138*[!DH], 344*[!DH])", "Dư Có chi tiết 338 (trừ 3387 đã ở Mã 319), 138, 344"),
 "321": ("GROSS_C(341*[!DH], 3431*[!DH])", "Dư Có chi tiết 341, 343 phải trả ≤ 12 tháng"),
 "322": ("BAL_C(352*[!DH])", "Dư Có chi tiết 352 ngắn hạn"),
 "323": ("BAL_C(353*)", "Dư Có 353"), "324": ("BAL_C(357*)", "Dư Có 357"), "325": ("BAL_C(171*)", "Dư Có 171"),
 "330": ("LINE(331) + LINE(332) + LINE(333) + LINE(334) + LINE(335) + LINE(336) + LINE(337) + LINE(338) + LINE(339) + LINE(340) + LINE(341) + LINE(342) + LINE(343) + LINE(344)", ""),
 "331": ("GROSS_C(331*[DH])", "Dư Có chi tiết 331 dài hạn"),
 "332": ("GROSS_C(131*[DH])", "Dư Có chi tiết 131 dài hạn"),
 "333": ("GROSS_C(333*[DH])", "Dư Có chi tiết 333 dài hạn"),
 "334": ("BAL_C(335*[DH])", "Dư Có chi tiết 335 dài hạn"),
 "335": ("BAL_C(3361*)", "Dư Có 3361"),
 "336": ("GROSS_C(3362*[DH], 3363*[DH], 3368*[DH])", "Dư Có chi tiết 3362, 3363, 3368 dài hạn"),
 "337": ("BAL_C(3387*[DH])", "Dư Có chi tiết 3387 dài hạn"),
 "338": ("GROSS_C(338*[DH], !3387*, 344*[DH])", "Dư Có chi tiết 338, 344 dài hạn"),
 "339": ("GROSS_C(341*[DH], 3431*[DH])", "Dư Có 341, 3431 dài hạn"),
 "340": ("BAL_C(3432*)", "Dư Có 3432"),
 "341": ("BAL_C(41112*[NO])", "Dư Có chi tiết 41112 cổ phiếu ưu đãi phân loại là nợ (nhãn NO)"),
 "342": ("BAL_C(347*)", "Dư Có 347"), "343": ("BAL_C(352*[DH])", "Dư Có chi tiết 352 dài hạn"), "344": ("BAL_C(356*)", "Dư Có 356"),
 "400": ("LINE(411) + LINE(412) + LINE(413) + LINE(414) + LINE(415) + LINE(416) + LINE(417) + LINE(418) + LINE(419) + LINE(420)", "B.1.4.g"),
 "411": ("BAL_C(4111*) - LINE(341)", "Dư Có 4111 (công ty cổ phần: = 411a + 411b)"),
 "411A": ("BAL_C(41111*)", "Dư Có 41111"),
 "411B": ("BAL_C(41112*[!NO])", "Dư Có chi tiết 41112 phân loại là vốn"),
 "412": ("-BAL_NET(4112*)", "Dư Có 4112; dư Nợ ghi âm"),
 "413": ("BAL_C(4113*)", "Dư Có 4113"), "414": ("BAL_C(4118*)", "Dư Có 4118"),
 "415": ("-BAL_D(419*)", "Dư Nợ 419, ghi âm"),
 "416": ("-BAL_NET(412*)", "Dư Có 412; dư Nợ ghi âm"),
 "417": ("-BAL_NET(413*)", "Dư Có 413; dư Nợ ghi âm"),
 "418": ("BAL_C(414*)", "Dư Có 414"), "419": ("BAL_C(418*)", "Dư Có 418"),
 "420": ("LINE(420A) + LINE(420B)", "Dư Có 421; dư Nợ ghi âm"),
 "420A": ("-BAL_NET(4211*) - OPEN_NET(4212*)", "Dư Có 4211 cuối kỳ + dư Có 4212 đầu kỳ báo cáo (lợi nhuận năm nay lũy kế đến đầu kỳ — BCTC quý); BCTC năm: 4212 đầu năm bằng 0 sau khi kết chuyển sang 4211, nên chỉ còn dư Có 4211. Dư Nợ ghi âm"),
 "420B": ("-BAL_NET(4212*) + OPEN_NET(4212*)", "Lợi nhuận sau thuế kỳ này: dư Có 4212 cuối kỳ trừ dư Có đầu kỳ báo cáo (BCTC năm: dư Có 4212). Dư Nợ ghi âm"),
 "440": ("LINE(300) + LINE(400)", "B.1.4.h"),
}
SUB = {"411A", "411B", "420A", "420B", "222", "223", "225", "226", "228", "229", "232", "233", "234", "235", "241", "242"}

def build(code, title, rows, code_col, formulas, legal, checks, subs=(), totals=()):
    official, order = names(rows, code_col)
    missing = [c for c in official if c not in formulas]
    extra = [c for c in formulas if c not in official]
    if missing or extra: raise SystemExit(f"{code}: formula/table mismatch missing={missing} extra={extra}")
    lines = []
    for item in order:
        if isinstance(item, tuple):
            lines.append({"code": f"H{len(lines) + 1:03d}", "name": item[1], "formula": None, "indent": 0, "bold": True, "note": None}); continue
        f, note = formulas[item]
        name = official[item]
        level = 0 if item in totals else (2 if item in subs else 1)
        lines.append({"code": item, "name": name, "formula": f, "indent": level, "bold": item in totals, "note": note or None})
    return {"code": code, "name": title, "category": "BCTC", "legalReference": legal, "lines": lines, "checks": checks}

rows = read("1577_t007.tsv")
b01 = build("B01-DN", "Báo cáo tình hình tài chính", rows, 2, B01,
            "Mẫu số B 01 - DN, Phụ lục IV TT 99/2025/TT-BTC; hướng dẫn lập: Phần B mục 1.4 (Công báo 1579+1580)",
            [{"rule": "LINE(280) = LINE(440)", "message": "Tổng cộng tài sản (280) = Tổng cộng nguồn vốn (440) — nếu lệch: kiểm tra đã kết chuyển kết quả chưa, TK nào có số dư chưa được chỉ tiêu nào lấy"}],
            SUB, {"100", "200", "280", "300", "400", "440"})

B02 = {
 "01": ("NET_C(511*[!BDSDT])", "Phát sinh Có 511 (không gồm bán, thanh lý BĐSĐT; trừ thuế gián thu ghi giảm 511), không tính bút toán kết chuyển"),
 "02": ("NET_D(521*)", "Các khoản giảm trừ doanh thu phát sinh trong kỳ (521)"),
 "10": ("LINE(01) - LINE(02)", ""),
 "11": ("NET_D(632*[!BDSDT])", "Giá vốn 632 phát sinh trong kỳ (không gồm BĐSĐT thanh lý)"),
 "20": ("LINE(10) - LINE(11)", ""),
 "21": ("NET_C(511*[BDSDT]) - NET_D(632*[BDSDT])", "Doanh thu bán, thanh lý BĐSĐT − giá trị còn lại, chi phí (TK chi tiết nhãn BDSDT)"),
 "22": ("NET_C(515*[!DGL]) + MAX0(NET_C(515*[DGL]) - NET_D(635*[DGL]))", "Doanh thu tài chính; chênh lệch tỷ giá đánh giá lại cuối kỳ trình bày thuần (nhãn DGL)"),
 "23": ("NET_D(635*[!DGL]) + MAX0(NET_D(635*[DGL]) - NET_C(515*[DGL]))", "Chi phí tài chính; lỗ thuần đánh giá lại tỷ giá"),
 "24": ("NET_D(635*[LV])", "Chi phí đi vay trong 635 (TK chi tiết nhãn LV)"),
 "25": ("NET_D(641*)", "Chi phí bán hàng"),
 "26": ("NET_D(642*)", "Chi phí quản lý doanh nghiệp"),
 "30": ("LINE(20) + LINE(21) + LINE(22) - (LINE(23) + LINE(25) + LINE(26))", ""),
 "31": ("NET_C(711*[!TLTS]) + MAX0(NET_C(711*[TLTS]) - NET_D(811*[TLTS]))", "Thu nhập khác; thanh lý, nhượng bán TSCĐ trình bày thuần (nhãn TLTS)"),
 "32": ("NET_D(811*[!TLTS]) + MAX0(NET_D(811*[TLTS]) - NET_C(711*[TLTS]))", "Chi phí khác; lỗ thuần thanh lý TSCĐ"),
 "40": ("LINE(31) - LINE(32)", ""),
 "50": ("LINE(30) + LINE(40)", ""),
 "51": ("NET_D(8211*)", "Chi phí thuế TNDN hiện hành (8211); thu nhập ghi âm"),
 "52": ("NET_D(8212*)", "Chi phí thuế TNDN hoãn lại (8212); thu nhập ghi âm"),
 "60": ("LINE(50) - (LINE(51) + LINE(52))", ""),
 "70": (None, "Lãi cơ bản trên cổ phiếu — công ty cổ phần tự tính theo hướng dẫn mục 3.20 (Công báo 1581+1582)"),
 "71": (None, "Lãi suy giảm trên cổ phiếu — tự tính theo hướng dẫn mục 3.21"),
}
rows = read("1577_t013.tsv")
b02 = build("B02-DN", "Báo cáo kết quả hoạt động kinh doanh", rows, 2, B02,
            "Mẫu số B 02 - DN, Phụ lục IV TT 99/2025/TT-BTC; hướng dẫn lập: Phần C mục 3 (Công báo 1579+1580, 1581+1582)", [],
            {"24"}, {"10", "20", "30", "40", "50", "60"})
for t in (b01, b02):
    with open(os.path.join(out, f"mau-{t['code']}.json"), "w", encoding="utf-8") as f:
        json.dump(t, f, ensure_ascii=False, indent=1)
    print(t["code"], len(t["lines"]), "dòng")

# ---------------------------------------------------------------- B03-DN (direct method)
CASH = "111*[!HCSD], 112*[!HCSD], 113*[!HCSD], 1281*[TDT,!HCSD], 1288*[TDT,!HCSD]"
B03 = {c: (f"CF({c})", "Luồng tiền mã " + c + " (dòng tiền 111/112/113 và tương đương tiền; phân loại theo mã chọn trên chứng từ, nhãn CFxx của TK đối ứng, hoặc mặc định theo TK đối ứng)")
       for c in ["01", "02", "03", "04", "05", "06", "07", "21", "22", "23", "24", "25", "26", "27", "31", "32", "33", "34", "35", "36", "61"]}
B03.update({
 "20": ("LINE(01) + LINE(02) + LINE(03) + LINE(04) + LINE(05) + LINE(06) + LINE(07)", ""),
 "30": ("LINE(21) + LINE(22) + LINE(23) + LINE(24) + LINE(25) + LINE(26) + LINE(27)", ""),
 "40": ("LINE(31) + LINE(32) + LINE(33) + LINE(34) + LINE(35) + LINE(36)", ""),
 "50": ("LINE(20) + LINE(30) + LINE(40)", ""),
 "60": (f"OPEN_D({CASH})", "Bằng Mã số 110 cột đầu kỳ của Báo cáo tình hình tài chính"),
 "70": ("LINE(50) + LINE(60) + LINE(61)", "Bằng Mã số 110 cột cuối kỳ của Báo cáo tình hình tài chính"),
})
rows = read("1577_t016.tsv")
b03 = build("B03-DN", "Báo cáo lưu chuyển tiền tệ (theo phương pháp trực tiếp)", rows, 2, B03,
            "Mẫu số B 03 - DN (phương pháp trực tiếp), Phụ lục IV TT 99/2025/TT-BTC; hướng dẫn lập: Phần D (Công báo 1581+1582)",
            [{"rule": f"LINE(70) = BAL_D({CASH})", "message": "Tiền và tương đương tiền cuối kỳ (70) = số dư cuối kỳ tiền và tương đương tiền (Mã 110 trên B01-DN). Nếu lệch: TK 111/112/113 phải khai báo Loại tiền; khoản tương đương tiền gắn nhãn TDT"}],
            (), {"20", "30", "40", "50", "70"})
with open(os.path.join(out, "mau-B03-DN.json"), "w", encoding="utf-8") as f:
    json.dump(b03, f, ensure_ascii=False, indent=1)
print("B03-DN", len(b03["lines"]), "dòng")

# ---------------------------------------------------------------- B03-DN (indirect method)
# Working-capital lines: OPEN_NET − BAL_NET is "decrease of an asset / increase of a liability" for both kinds of account;
# FXR() adds back the exchange-rate revaluation booked on them (non-cash, already removed with Mã 04).
REC = "131*[!DT], 133*, 136*[!DT], 138*[!DT], 141*, 244*[!DT]"
PAY = "331*[!DT], 333*, !3334*, 334*, 335*[!LV,!DT], 336*[!DT], 337*, 338*[!DT], 344*[!DT]"
# 131 / 331 are split by the side of each partner's balance (Mã 09 takes debit balances, Mã 11 credit balances), as the guide lists
# 331 prepayments under receivables and 131 customer advances under payables; the sum 09 + 11 equals the net change.
PARTY = "131*[!DT], 331*[!DT]"
REC_OTHER = "133*, 136*[!DT], 138*[!DT], 141*, 244*[!DT]"
PAY_OTHER = "333*, !3334*, 334*, 335*[!LV,!DT], 336*[!DT], 337*, 338*[!DT], 344*[!DT]"
FUNDS = "353*, 356*, 357*"
B03I = {c: B03[c] for c in ["21", "22", "23", "24", "25", "26", "27", "30", "31", "32", "33", "34", "35", "36", "40", "50", "60", "61", "70"]}
B03I.update({
 "01": ("NET_C(511*, 515*, 711*) - NET_D(521*, 632*, 635*, 641*, 642*, 811*)", "Bằng Mã số 50 của Báo cáo kết quả hoạt động kinh doanh"),
 "02": ("PS_D(6234*, 6274*, 6414*, 6424*, 6*[KH]) + PS_C(242*[DT])",
        "Khấu hao ghi vào chi phí (6234, 6274, 6414, 6424 và TK chi phí gắn nhãn KH, gồm khấu hao BĐSĐT trên TK 632 chi tiết) + phân bổ tiền thuê đất trả một lần (242 nhãn DT); trừ bút toán đảo. Khấu hao vào XDCB (241) hoặc ghi giảm quỹ không có trong các TK này nên tự loại trừ"),
 "03": ("OPEN_NET(229*, 352*) - BAL_NET(229*, 352*)", "Chênh lệch số dư cuối kỳ – đầu kỳ các khoản dự phòng (229, 352): tăng ghi dương, giảm ghi âm"),
 "04": ("NET_D(635*[DGL]) - NET_C(515*[DGL])", "Lãi (ghi âm) / lỗ (ghi dương) chênh lệch tỷ giá đánh giá lại cuối kỳ (515, 635 chi tiết nhãn DGL)"),
 "05": ("NET_D(811*[TLTS]) - NET_C(711*[TLTS]) + NET_D(635*[DT]) - NET_C(515*[DT]) + NET_D(632*[BDSDT]) - NET_C(511*[BDSDT])",
        "Lãi (ghi âm) / lỗ hoạt động đầu tư: thanh lý TSCĐ (711, 811 nhãn TLTS), lãi cho vay, tiền gửi, cổ tức, lãi lỗ bán khoản đầu tư (515, 635 nhãn DT), bán BĐSĐT (511, 632 nhãn BDSDT)"),
 "06": ("NET_D(635*[LV])", "Chi phí đi vay ghi nhận trong kỳ (635 nhãn LV) — khớp Mã 24 B02-DN"),
 "07": (f"MOV_C({FUNDS} ; 6*, 8*) - MOV_D({FUNDS} ; 5*, 6*, 7*)", "Trích lập (dương) / hoàn nhập (âm) Quỹ phát triển KH&CN (356), Quỹ bình ổn giá (357) vào kết quả kinh doanh"),
 "08": ("LINE(01) + LINE(02) + LINE(03) + LINE(04) + LINE(05) + LINE(06) + LINE(07)", ""),
 "09": (f"OPEN_NET({REC_OTHER}) - BAL_NET({REC_OTHER}) + OPEN_GROSS_D({PARTY}) - GROSS_D({PARTY}) + FXR({REC})",
        "Chênh lệch đầu kỳ – cuối kỳ các khoản phải thu hoạt động kinh doanh: 133, 136, 138, 141, 244 và số dư Nợ theo từng đối tượng của 131 (phải thu khách hàng) và 331 (trả trước cho người bán, gồm thuế GTGT đầu vào) — Phần D mục 4.1.2 Mã số 09; trừ TK chi tiết nhãn DT (phải thu đầu tư); không kể đánh giá lại tỷ giá"),
 "10": ("OPEN_NET(15*) - BAL_NET(15*)", "Chênh lệch đầu kỳ – cuối kỳ hàng tồn kho (151–158, không gồm dự phòng 2294)"),
 "11": (f"OPEN_NET({PAY_OTHER}) - BAL_NET({PAY_OTHER}) + GROSS_C({PARTY}) - OPEN_GROSS_C({PARTY}) + FXR({PAY})",
        "Chênh lệch cuối kỳ – đầu kỳ các khoản phải trả hoạt động kinh doanh: 333 trừ 3334, 334, 335 trừ lãi vay nhãn LV, 336, 337, 338, 344 và số dư Có theo từng đối tượng của 331 (phải trả người bán) và 131 (người mua trả tiền trước) — Phần D mục 4.1.2 Mã số 11; trừ TK chi tiết nhãn DT (phải trả đầu tư, tài chính); không kể đánh giá lại tỷ giá"),
 "12": ("OPEN_NET(242*[!DT]) - BAL_NET(242*[!DT])", "Chênh lệch đầu kỳ – cuối kỳ chi phí chờ phân bổ 242 (trừ tiền thuê đất trả một lần — nhãn DT)"),
 "13": ("OPEN_NET(121*) - BAL_NET(121*)", "Chênh lệch đầu kỳ – cuối kỳ chứng khoán kinh doanh 121"),
 "14": ("CF(04)", "Tiền chi trả chi phí đi vay (luồng tiền mã 04: TK đối ứng nhãn LV hoặc mã chọn tay)"),
 "15": ("CF(05)", "Tiền nộp thuế TNDN (luồng tiền mã 05: đối ứng 3334)"),
 "16": (f"MOV_C({FUNDS} ; {CASH})", "Tiền thu ghi tăng các quỹ 353, 356, 357"),
 "17": (f"-MOV_D({FUNDS} ; {CASH}, 334*, 338*)", "Tiền chi (hoặc chuyển phải trả 334, 338) từ các quỹ 353, 356, 357, ghi âm"),
 "20": ("LINE(08) + LINE(09) + LINE(10) + LINE(11) + LINE(12) + LINE(13) + LINE(14) + LINE(15) + LINE(16) + LINE(17)", ""),
})
rows = read("1577_t019.tsv")
b03i = build("B03-DN-GT", "Báo cáo lưu chuyển tiền tệ (theo phương pháp gián tiếp)", rows, 2, B03I,
             "Mẫu số B 03 - DN (phương pháp gián tiếp), Phụ lục IV TT 99/2025/TT-BTC; hướng dẫn lập: Phần D mục 4.1.2, 4.2, 4.3 (Công báo 1581+1582)",
             [{"rule": f"LINE(70) = BAL_D({CASH})", "message": "Tiền và tương đương tiền cuối kỳ (70) = số dư cuối kỳ tiền và tương đương tiền (Mã 110 trên B01-DN). Nếu lệch: TK 111/112/113 phải khai báo Loại tiền; khoản tương đương tiền gắn nhãn TDT"},
              {"rule": "LINE(20) = CF(01, 02, 03, 04, 05, 06, 07)", "message": "Lưu chuyển tiền thuần từ HĐKD tính gián tiếp (20) = tính trực tiếp từ sổ tiền. Nếu lệch: có khoản phải thu/phải trả liên quan đầu tư, tài chính chưa gắn nhãn DT (và CFxx), khấu hao chưa ghi vào TK chi phí khấu hao/nhãn KH, hoặc dòng tiền chọn mã đầu tư/tài chính nhưng đối ứng TK công nợ kinh doanh"}],
             ("02", "03", "04", "05", "06", "07"), {"08", "20", "30", "40", "50", "70"})
with open(os.path.join(out, "mau-B03-DN-gian-tiep.json"), "w", encoding="utf-8") as f:
    json.dump(b03i, f, ensure_ascii=False, indent=1)
print("B03-DN-GT", len(b03i["lines"]), "dòng")