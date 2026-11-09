# Báo cáo nghiên cứu: Tách EDZ EPLAN nhiều thiết bị trên Linux (không EPLAN runtime)

**Dự án:** edzsplit · **Phiên bản:** 0.1.0-poc · **Ngày:** 2026-11-09 · **Bản quyền:** MIT · **Tác giả:** edzsplit contributors

> **TRẠNG THÁI CHỨNG MINH (đọc trước tiên):** Tool đã được kiểm chứng trọn vẹn ở mức **cấu trúc** (7z integrity, manifest parse, mọi tham chiếu item resolve được, không path traversal) trên dữ liệu hãng thật: EKF (4.540 part), Theben (86 EDZ con), Rittal/FESTO (parts.edz). Việc **import một EDZ đầu ra vào Parts Database thật của EPLAN CHƯA được thực hiện** trong phiên làm việc này — máy phát triển không tạo/mở được parts database scratch (chi tiết §10). Vì tiêu chí thành công của đề bài yêu cầu import thật, báo cáo này **không tuyên bố bài toán đã được giải quyết**; nó chứng minh pipeline đọc/lập kế hoạch/tách/ghi/kiểm chứng hoạt động, và chỉ rõ phần còn thiếu đúng một bước: chạy import oracle (§10.2).

---

## 1. Tóm tắt điều hành

Một file `.edz` của EPLAN chứa các "package" (part, manufacturer, construction, connection points, accessory list…) và các "item" (partxml, macro, picture, document, contour). Có hai hình thái:

* **Case A** — archive đã chứa sẵn các EDZ con (ví dụ `Theben.7z`).
* **Case B** — một EDZ chứa hàng nghìn thiết bị (ví dụ `EKF.edz`).

edzsplit đọc EDZ (7z hoặc ZIP), dựng dependency graph part → tài nguyên, tách thành một EDZ hợp lệ cho mỗi part, ghi log có thể replay, và tự kiểm chứng cấu trúc output. Toàn bộ pipeline chạy trên Linux (Ubuntu 24.04), **không EPLAN, không Windows, không license**.

**Đã chứng minh:** đọc/repair/kiểm chứng 100 % tham chiếu 17.658 refs của EKF.edz (UTF‑8-aware); tách full 4.540 part trong **4 phút 06 giây**, 0 lỗi; 76/76 mẫu verify OK; `--resume` hoạt động (4.540 skipped); Case A trích 86/86 EDZ với verify OK; byte-identity của 60/60 member không phải manifest giữa source `parts.edz` và các EDZ tách ra.

**Chưa chứng minh:** import thật vào Parts Database (môi trường bị chặn — §10). Đầu ra ghi theo 7z "Copy" (không nén) — lớn hơn ~2× nhưng tương thích byte với mọi reader 7z; định dạng này đã được round‑trip qua `7z t/l/x` thật.

## 2. Vấn đề và phạm vi

Yêu cầu ban đầu (đề bài): chương trình Linux độc lập, đọc EDZ ZIP/7Z, nhận diện toàn bộ thiết bị/mã sản phẩm, trích xuất metadata từng part, xác định tài nguyên liên quan, tạo một EDZ hợp lệ cho mỗi part, bảo toàn thông tin/tài nguyên, kiểm tra hợp lệ, xác minh import vào EPLAN. Xử lý dữ liệu multi‑GB, streaming, có resume.

Ngoài phạm vi: tải từ EPLAN Data Portal; sửa chữa database nguồn; đồng bộ ngược vào EPLAN.

## 3. Giải phẫu EDZ (phân tích thực địa)

Nguồn phân tích: 86 EDZ của Theben, `parts.edz` (15 part + 1 construction), `rt-05-accessorylists.edz` (206 package), `smoke-232715.edz`, `import-smoke.edz`, `EKF.edz` (4.540 part).

### 3.1. Container

* Container là **7z chuẩn** (EKF: `LZMA:12`, **non-solid**, 6.057 blocks; Theben.7z: `LZMA2:24m`, solid). ZIP cũng đọc được (định dạng cũ).
* Tên entry trong file chính thức dùng **forward slash** (`items/macro/…`) — kiểm chứng bằng hexdump đầu ra của chính EDZ chính thức (`Theben\THB.0060002.edz`). `7z` trên Windows hiển thị thành `\`; không phải backslash thật.
* `manifest.xml` nằm ở **cuối archive**; phần dữ liệu (`items\…`) đứng trước (thứ tự giống official export).
* `manifest.xml` tham chiếu item theo đường dẫn dạng **backslash** trong thuộc tính `locator` (ví dụ `EKF\30102Р.png`).

### 3.2. manifest.xml

```xml
<manifest version="2.0">
 <packages>
  <package type="part" key="EKF.10201" name="EKF.10201">
   <items>
    <item type="partxml" name="part" locator="EKF.10201.part.xml"/>
    <item type="partxml" name="manufacturer" locator="EKF.manufacturer.xml"/>
    <item type="picture" name="picturefile" locator="EKF\10201.png"/>
   </items>
  </package>
  …
```

* Package types quan sát được: `part`, `manufacturer`, `construction`, `connectionpoints`, `accessorylist`, `accessoryplacement`.
* Item types: `partxml`, `macro`, `picture`, `document`, `contour` (contour chưa gặp trong các mẫu nhưng được parser xử lý tổng quát).
* Item `name` mang ngữ nghĩa EPLAN: `part`, `manufacturer`, `supplier`, `graphicmacro` (=`P_ARTICLE_MACRO`, 3D macro), `groupsymbolmacro` (=`P_ARTICLE_GROUPSYMBOLMACRO`, schematic), `picturefile`, `connectionpoints`, `construction`, `accessorylist`, `accessoryplacement`, `externalDocument1..20`.
* Mỗi package `part` tự chứa item `manufacturer`/`supplier` trỏ tới cùng một `*.manufacturer.xml` — các EDZ chính thức **lặp lại** member dùng chung ở mọi package (không dedupe) → hãy dedupe khi ghi, giữ manifest cho từng package (xem §6).

### 3.3. Tham chiếu giữa part và tài nguyên

Thuộc tính trong `*.part.xml`:

| Thuộc tính | Ý nghĩa |
|---|---|
| `P_ARTICLE_PARTNR` | Mã part (khóa package) |
| `P_ARTICLE_MACRO` | `$(MD_MACROS)\…\xxx.ema` — macro 3D (item `graphicmacro`) |
| `P_ARTICLE_GROUPSYMBOLMACRO` | Macro schematic (item `groupsymbolmacro`) |
| `P_ARTICLE_PICTUREFILE` | `$(MD_IMG)\…\xxx.jpg` (item `picturefile`) |
| `P_ARTICLE_EXTERNAL_DOCUMENT_n` | `$(MD_DOCUMENTS)\…` (item `document`) hoặc URL thuần (không phải file) |
| `P_ARTICLE_REF_CONSTRUCTION_NAME` | Khóa package `construction` |
| `P_ARTICLE_REF_TERMINAL_NAME` | Tên base cho item `connectionpoints` (`<name>.connectionpoints.xml`) |
| `P_ARTICLE_IS_ACCESSORY`, `P_ARTICLE_ACCESSORYID` | Vật tư phụ kiện (không phải khóa package) |

`accessorylist` (file `*.accessorylist.xml`) liệt kê các phần tử `<accessorylistposition partnr="…" variant="…" placement="…"/>` trỏ **ngược** từ accessory list tới các part — đây là cạnh **ngược** quan trọng cho closure: khi tách một part là accessory, cần giữ cả accessory list chứa nó. `construction` gồm các `drillingPosition`, không tham chiếu part. `connectionpoints` (`*.connectionpoints.xml`) thuộc về package part. `externalDocument` có thể là file thật (`$(MD_DOCUMENTS)\Siemens\…\3SB34000B_de.pdf`, tồn tại trong archive) hoặc URL.

### 3.4. Tình trạng dữ liệu mẫu

| Mẫu | Size | Packages | Items refs | Ghi chú |
|---|---|---|---|---|
| EKF.edz | 265.109.028 B | 4540 part | 17.658 | 100 % refs resolve trực tiếp với UTF‑8-aware listing |
| Theben.7z | 20.499.119 B | 86 EDZ lồng | — | Case A |
| parts.edz | 9.834.942 B | 15 part + 1 construction | 76 | 1 construction dùng **chung key với part FES.195716** |
| rt-05-accessorylists.edz | 110.755.739 B | 172 part, 23 accessorylist, 11 construction | 1.238 | 95+95+10 externalDocument; 35 connectionpoints |
| smoke-232715.edz | 6.043.479 B | 27 part | 96 | 2 part Siemens giàu tài nguyên |
| import-smoke.edz | 1.823 B | 1 part | 3 | file nhỏ dùng làm oracle |

**Ghi chú sửa lỗi phân tích:** phân tích ban đầu với codepage OEM báo 421 refs "hỏng mojibake" phải repair bằng `_`. Với listing `-scc UTF-8`, **toàn bộ 17.658 refs resolve trực tiếp**; mọi kết quả "mojibake" đều là artifact hiển thị của OEM codepage. Cơ chế repair `non-ASCII → _` vẫn được giữ làm fallback nhưng không cần thiết cho EKF.

## 4. Case A

`Theben.7z` chứa `Theben\THB.*.edz` (86 file). `edzsplit case-a` giải nén nguyên vẹn và chạy structural verify từng file: **86/86 OK** (xem evidence). Đây là thao tác pass-through an toàn; không đọc nội dung.

## 5. Case B: kiến trúc và thuật toán

### 5.1. Pipeline

```
detect → list (7z -slt) → index (archive members, sanitize fallback)
      → manifest parse → cache extraction (một lượt, vào đĩa)
      → graph (payload refs + reverse accessory-list edges)
      → plan per part (closure) → clone manifest (locator rewrite khi repair)
      → write (managed 7z writer) → verify → JSONL state + resume
```

* **Listing một lượt** qua `7z l -slt -sccUTF-8` (tên UTF-8 chính xác).
* **Extraction một lượt** vào `<out>/.work-<runid>/cache` rồi đọc payload từ đĩa (tránh 4540 process 7z); cache bị xoá sau run (trừ `--keep-cache`).
* **Manifest clone**: giữ package/item của closure; locator được viết lại **chỉ** khi item resolve được qua sanitize fallback; mọi rewrite đều log `locator.repaired`/`payload.rewritten`.

### 5.2. Dependency graph

Node: package (`pkg:<type>:<key>`) và item (`item:<path>`). Edges:

1. package → item (sở hữu theo manifest);
2. item `part.xml` → refs payload (macro/groupsymbolmacro/picture/document → resolve qua `$(MD_MACROS|IMG|DOCUMENTS)` → member archive; construction → package `construction` theo khóa (hoặc sanitize); connectionpoints → item `connectionpoints` theo tên; accessory list đó) — đệ quy;
3. accessorylist package → item `accessorylist`/`accessoryplacement` (placement qua tên) — đệ quy;
4. reverse-context: part ← accessory lists có `<accessorylistposition partnr="KEY">` (sanitize retry).

**Closure modes**
* `none` — part + item manifest của nó.
* `related` (đề xuất cân nhắc) — seed part + tài nguyên trực tiếp + accessory lists tham chiếu tới nó + item của chúng (không mở rộng sang parts khác).
* `full` (mặc định theo yêu cầu) — closure đệ quy tới fixpoint: mọi part kéo theo accessory lists tham chiếu nó, mọi construction/connectionpoints. **Lưu ý kích thước:** với kho part lớn, `full` có thể kéo theo phần đáng kể catalog khi accessory list đan nhiều part với nhau; `--max-closure-ratio` cảnh báo khi output vượt r× nguồn; `--dry-run` lập kế hoạch trước khi ghi. EKF không có accessory list nên `full` và `related` trùng nhau về kích thước ở đây (4540 outputs, ~757 MB).

**Tìm đúng package khi trùng khóa (phát hiện & sửa):** `parts.edz` có `part FES.195716` và `construction FES.195716` **cùng khóa**. Bản đầu keyed toàn cục theo key đã shadow construction → item `FES.195716.construction.xml` bị rơi. Fix: khóa scoped theo `(type,key)` (`FindPackage("construction", …)`, `FindPart`, `_packagesByKey: key → List<Package>`). Sau fix, 15 outputs verify OK và construction được đóng đúng vào EDZ của part `FES.195716`.

**Đa chủ sở hữu item (phát hiện & sửa):** `EKF.manufacturer.xml` do cả 4.540 package part cùng sở hữu. Bản đầu `_itemOwners[path] = owner` (ghi đè) → part 2 lọt vào closure của part 1 (output 5 entries của SYN.001 chứa cả SYN.002; collision ở writer). Fix: `path → List<owners>`; owner-follow **chỉ** làm cho item cấu trúc (`connectionpoints`, `accessorylist`, `accessoryplacement`, `construction`, `contour`); item asset (picture/macro/document/manufacturer) là leaf, không owner-follow → không nổ catalog.

### 5.3. Writer (managed, không nén — Copy coder)

* EPLAN EDZ là 7z; `tools/…` thử dùng thư viện nén chuẩn: `SharpCompress` đọc được 7z nhưng **không ghi được**; viết LZMA từ đầu không khả thi/không an toàn.
* Writer tự chế (port từ writer của tác giả trong Tool-Oumijia): một folder, một packed stream, coder `Copy`, entry names UTF-16LE **forward slashes** (khớp official), CRCs được vá (StartHeaderCRC). Round-trip qua `7z t` của 7-Zip 23.01: OK; file đọc ngược khớp byte; verify tool mở được.
* manifest.xml ghi **cuối** (giống official export).
* **Trade-off:** không nén → output lớn hơn (ví dụ 4540 outputs = 757 MB so với archive 265 MB nén). Chấp nhận được cho v0.1; nút thắt tiếp theo là LZMA writer qua `7z` ngoài với tên entry ép `/` (khả thi: staging + danh sách file + patch header CRC) hoặc tham chiếu LZMA SDK native.

### 5.4. Repair có kiểm soát

Nếu item ref không resolve trực tiếp, thử sanitize `non-ASCII → _`; chỉ rewrite khi sanitize tồn tại; ghi log từng lần (`locator.repaired`, `payload.rewritten` trong `--fix-refs auto`). Không bao giờ đổi tên đoán.

### 5.5. Resume và quarantine

`.edzsplit-state.jsonl` (append + `Flush(true)`) ghi `{partnr,status,file,sha256}`; `--resume` bỏ qua phần `done`. Part lỗi: quarantine/report, exit code 3 (không dừng batch trừ khi `--fail-fast`). Thiết kế an toàn cho batch nhiều nghìn part.

## 6. EDZ writer & kiểm chứng

Ba tầng kiểm chứng độc lập EPLAN API:

1. **Structural verify** (`edzsplit verify`): `7z t`; parse manifest; mọi item ref resolve (direct/repaired); size/path traversal guard; format version.
2. **Round-trip container**: synthetic EDZ do chính writer tạo → `7z t`/`7z l`/`7z x` đọc lại OK (selftest); official `parts.edz` → tách → member bytes so sánh SHA256: **60/60 khớp tuyệt đối**.
3. **Import oracle** (`tools/EplanEdzValidator`): mở/tạo scratch parts DB qua `MDPartsManagement`, import bằng `IXPartsImportExportEdz` + scheme `Standardvorgabe EPLAN`, assert `ExistsPart` + delta. **Chưa chạy được trên máy này** (§10).

## 7. Kết quả kiểm thử

| ID | Test | Kết quả |
|---|---|---|
| T0-lite | `parts.edz` → tách 15 part | 15/15 EDZ verify OK; 60/60 non-manifest member byte-identical với source; construction vào đúng EDZ `FES.195716` |
| T1 | `smoke-232715.edz` 2 part Siemens (+1 Rittal) | 3/3 verify OK (9, 9, 6 items) |
| T2 | EKF `--only EKF.30102Р,EKF.ILR-10DR-230A,EKF.10201` | 3/3 verify OK (ký tự Cyrillic) |
| T3 | `parts.edz` construction closure | đóng đúng part `FES.195716` |
| T4 | Theben Case A | 86/86 extract, verify OK |
| T6 | EKF full (4.540) | 4.540 written, 0 failed, 757 MB, 4m06s; spot-check 76/76 OK; resume: 4.540 skipped |
| T7 | Negative: archive hỏng/không 7z | `split` báo lỗi rõ ràng, exit code 4 |
| T0 | Re-emit identity & **import EPLAN** | **CHƯA THỰC HIỆN (bị chặn — §10)** |

Chi tiết lệnh replay tại `docs/EVIDENCE.md`.

## 8. Bảy câu hỏi nghiên cứu

1. **Có project GitHub nào có EDZ writer hoàn chỉnh không cần EPLAN?** — Không tìm thấy (EplanEdzManager đọc + gọi EPLAN để export; các project import đều gọi EPLAN/Windows API; QElectroTech đọc một số cấu trúc EDZ). Khoảng trống này chính là lý do tồn tại của edzsplit.
2. **Có thể xác định đầy đủ chuẩn EDZ từ export chính thức không?** — Có, đủ để **đọc, clone, viết**: container (7z), manifest schema (`version="2.0"`, packages/items, name↔attribute mapping), thứ tự entry, tên `/`, manifest cuối, MD variables. Giới hạn: round-trip 100 % byte-identical là không thể/không cần thiết (manifest clone khác format nguồn); mục tiêu là **import được** — tiêu chí này chưa chạy được, xem §10.
3. **Làm sao xây dependency graph?** — §§5.2–5.3: thuộc tính part-XML + reverse accessory list + BFS fixpoint + closure modes + owner tagging (leaf vs structural).
4. **Python hay C#/C++ trên Linux?** — Chọn **C# (.NET 8)**: Linux-native, không EPLAN runtime, không package bên thứ ba; writer managed; 7z CLI cho test/extract. Python hoàn toàn khả thi cho phần reader; C++ chỉ cần nếu muốn nhúng LZMA hardware-accelerated sau này.
5. **PoC tách 2 thiết bị và kiểm chứng import?** — PoC tách 2 (và 27) part **đã chạy**, output verify structural OK; **import thật chưa thực hiện được** vì Parts DB trên máy này bị khóa/không tạo scratch được. Đây là khoảng cách duy nhất còn lại so với tiêu chí §7 của đề bài.
6. **Chức năng nào bắt buộc phải dùng EPLAN API?** — Chỉ còn: (a) import oracle (chứng minh import OK), (b) export parts database có thẩm quyền (golden master để so sánh), (c) repair các package bị EPLAN bỏ & validate lại. Split/inspect/verify KHÔNG cần.
7. **Nếu Linux-only chưa khả thi, fallback nào?** — Kiến trúc **"Linux pipeline + Windows oracle"**: CI chạy `edzsplit`+structural verify ở mọi nơi; một Windows runner có EPLAN (có thể offline) chạy oracle (`tools/EplanEdzValidator`) + so sánh SHA256 manifest/inputs trước/sau; release chỉ gắn tag khi oracle xanh. Nếu EPLAN không khả dụng, dùng **golden-file comparison** (byte so sánh member, như T0-lite đã làm) làm kiểm chứng tối thiểu, kèm disclaimer rõ ràng.

## 9. Rủi ro và giảm thiểu

| Rủi ro | Giảm thiểu |
|---|---|
| EPLAN import "OK" nhưng bỏ qua tài nguyên thiếu | Assert `ExistsPart` + đếm delta DB + đọc field; log `S373012`-style; errors.txt pattern |
| Manifest khác phiên bản EPLAN | Clone giữ nguyên attribute gốc, không sinh schema mới |
| Tên file mojibake / Cyrillic | UTF-8 listing; fallback sanitize; log từng rewrite; không đoán |
| Writer khác biệt (solid/coder) | Writer Copy non-solid + round-trip `7z t` + official hexdump đối chiếu; option `--no-verify` không được khuyến khích |
| Multi-GB & interruption | Cache trên đĩa + streaming + SHA256 state + resume |
| Batch chết vì 1 part hỏng | quarantine + exit 3 + `--fail-fast` khi cần |
| `full` closure kéo catalog | `--max-closure-ratio`, `--dry-run`, `--only`, `related` |
| Nhầm DB chính | Validator hard-guard token `ESS_part001` + chỉ cho `%TEMP%\edzsplit-oracle` |

## 10. Giới hạn và những điều chưa làm

### 10.1. Môi trường EPLAN của máy này

* Host install `C:\Program Files\EPLAN\Platform\2022.0.3` thiếu `Cfg\install.xml` → `EPLAN.exe` báo *"Necessary XML-file … doesn't exist!"* và thoát. Instance đang chạy (SessionId 2) hoạt động được nhưng không tạo được parts database mới trong UI đã thử (ribbon owner-drawn, không nút Database; user không biết đường dẫn tạo DB; Task Manager/DLL đều bị ACL chặn đọc).
* Parts DB: `ESS_part001.adb` = **1 byte, ACL deny read**; `ESS_part001.alk` = 42 bytes UTF‑16 redirect `"ESS_part001.adb"`. Không có bản sao hợp lệ, không template để tạo scratch.
* `MDPartsManagement.CreateDatabase` ngoài EPLAN: trả về im lặng, file 0 byte, `OpenDatabase` ném `ApplicationException` rỗng — **không tạo DB được headless**.
* Hệ quả: **không thực hiện được import oracle trên máy này trong phiên này.**

### 10.2. Việc cần làm để đóng tiêu chí

1. Một máy/VM có EPLAN Electric P8 2022.0.3 hoạt động đầy đủ, tạo database **scratch** trong `%TEMP%` (UI: Master data → Parts → Management — tạo DB mới; hoặc admin giải khóa `Platform\2022.0.3\Cfg\install.xml`).
2. Chạy oracle (`tools/EplanEdzValidator`): `create` → `import` EDZ đầu ra → assert `parts-before/after`, `ExistsPart`, field macro/picture/document.
3. Nếu import lỗi: dùng golden-file fallback, ghi rõ trong báo cáo.
4. Thêm xunit suite + CI matrix (ubuntu-24.04; runner Windows EPLAN self-hosted).

### 10.3. Giới hạn kỹ thuật khác

* Writer không nén (Copy) — output lớn; chưa LZMA.
* `related` vs `full` cần benchmark với catalog thật có accessory lists đan nhau.
* `--jobs` (parallel) chưa triển khai (tuần tự an toàn).
* Chỉ kiểm chứng với EPLAN 2022 semantics; các phiên bản khác chưa test.
* Chưa chạy trên Linux thật (mới cross-compile linux-x64; không có Docker/WSL trên máy này).
* EDZ ZIP container: đọc được (7z CLI auto-detect) nhưng writer chỉ xuất 7z.

## 11. Lộ trình

* v0.2: LZMA writer (spawn `7z a` với tên ép `/`, patch header CRC hoặc dùng LZMA SDK); parallel `--jobs`; xunit CI; Linux UAT.
* v0.3: golden-file harness (so sánh SHA256 member) thành `edzsplit verify --compat <official.edz>`; EPLAN oracle CI self-hosted.
* v1.0: import UAT xanh trên EPLAN 2022 (+ 2026 nếu có) — tiêu chí đóng đề bài.

## 12. Phụ lục: lệnh replay

Xem `docs/EVIDENCE.md` — toàn bộ command + output (đã scrub đường dẫn nội bộ).

## 13. Tài liệu tham khảo

* EPLAN Electric P8 2022.0.3 (cài cục bộ) — nguồn phân tích format; `parts.edz` (export EDZ chính thức của người dùng); `rt-*.edz` (export EPLAN của Tool Oumijia).
* 7-Zip 23.01 — `7z l/t/x`, `-slt`, `-sccUTF-8`.
* Source tham khảo khảo sát: Tool-Oumijia-EPLAN (`EdzArchive.cs`, `EdzPackageReader.cs`, `SevenZipCopyWriter.cs`), EplanEdzManager (đọc + EPLAN export).

<sub>Sinh tự động từ evidence tại `docs/EVIDENCE.md`. Đường dẫn tuyệt đối trong ví dụ đã được thay bằng `<workspace>`.</sub>
