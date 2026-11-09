# edzsplit

**Tách một file EPLAN EDZ chứa nhiều thiết bị thành một EDZ riêng cho mỗi part — chạy trên Linux thuần túy, không cần EPLAN, không cần Windows.**

[English](README.md) · [Báo cáo nghiên cứu (VI)](docs/REPORT.vi.md) · [Research report (EN)](docs/REPORT.en.md) · [Evidence](docs/EVIDENCE.md)

> **Ghi chú thành thật về mức chứng minh:** Tool đã chạy trọn vẹn pipeline *tách cấu trúc* và được kiểm chứng bằng máy (archive → EDZ từng part → verify lại cấu trúc) trên dữ liệu hãng thật (EKF, Theben, Rittal/FESTO; tách full 4.540 part trong ~4 phút). **Việc import một EDZ đầu ra vào Parts Database thật của EPLAN CHƯA được chạy trên máy phát triển này** (bản cài EPLAN ở đây không tạo/mở được parts database scratch — xem [báo cáo §10](docs/REPORT.vi.md#10-giới-hạn-và-những-điều-chưa-làm)). Vì vậy tuyên bố "import OK" vẫn CHƯA được chứng minh cho đến khi bạn chạy oracle ở [docs/EVIDENCE.md](docs/EVIDENCE.md). Hãy đọc báo cáo trước khi áp dụng.

## Tool làm gì

File `.edz` của EPLAN dùng để trao đổi parts/articles với EPLAN Parts Database. Có hai hình thái:

* **Case A** — archive đã chứa sẵn nhiều EDZ từng part (ví dụ `Theben.7z`). Xử lý bằng `edzsplit case-a`.
* **Case B** — *một* EDZ chứa hàng nghìn part và tài nguyên (macro, ảnh, tài liệu), ví dụ `EKF.edz` (265 MB, 4.540 part, 17.658 tham chiếu item). Đây là bài toán khó mà `edzsplit` giải quyết: phân tích phụ thuộc + writer tạo một EDZ hợp lệ cho mỗi part.

## Tính năng

* Đọc EDZ chính thức của EPLAN (container 7z, `manifest.xml` + `items/…`, tên entry UTF-16) — cũng đọc được container ZIP.
* Nhận diện toàn bộ package (`part`, `manufacturer`, `construction`, `connectionpoints`, `accessorylist`…) và loại item (`partxml`, `macro`, `picture`, `document`, `contour`…).
* Dựng dependency graph part → tài nguyên từ thuộc tính part-XML (`P_ARTICLE_MACRO`, `P_ARTICLE_GROUPSYMBOLMACRO`, `P_ARTICLE_PICTUREFILE`, `P_ARTICLE_EXTERNAL_DOCUMENT_n`, `P_ARTICLE_REF_CONSTRUCTION_NAME`, `P_ARTICLE_REF_TERMINAL_NAME`, accessory list/placement…).
* `--closure full` (mặc định): part + tài nguyên trực tiếp + construction/connection points liên đới (đệ quy) + accessory list có tham chiếu tới part (và thành viên của chúng). Có thêm `related` và `none`.
* **Streaming**: trích xuất một lượt vào cache trên đĩa; không nạp toàn bộ vào RAM; ghi output từng entry. Hỗ trợ nguồn multi-GB (xem benchmark trong evidence).
* **Resume**: `.edzsplit-state.jsonl` ghi trạng thái từng part; `--resume` bỏ qua phần đã xong sau khi bị gián đoạn.
* **Repair có kiểm soát**: ánh xạ tên không-ASCII ↔ tên đã sanitize (ví dụ Cyrillic ↔ bản archive dùng `_`), chỉ áp dụng khi tên sanitize thực sự tồn tại — không đoán. `--fix-refs auto` chỉ ghi lại payload khi thật sự cần và log mọi lần rewrite.
* **Verify cấu trúc** output: kiểm tra toàn vẹn 7z, parse manifest, mọi item ref đều resolve được, chặn path traversal.
* **Quarantine**: package hỏng được báo cáo và bỏ qua (exit code 3); có `--fail-fast`; batch 4.540 part không bao giờ bị hỏng chỉ vì một package.
* **Không dependency**: .NET 8, gọi `7z` hệ thống (p7zip trên Linux) để test/giải nén; writer EDZ là writer 7z managed (no compression) với tên entry tương thích byte với file chính thức.

## Cài đặt

### Ubuntu Server 24.04

```bash
sudo apt update
sudo apt install -y p7zip-full
# Cách 1: chạy từ source
sudo apt install -y dotnet-sdk-8.0
dotnet publish src/edzsplit/edzsplit.csproj -c Release -r linux-x64 --self-contained -o /usr/local/bin/edzsplit-bin
sudo ln -sf /usr/local/bin/edzsplit-bin/edzsplit /usr/local/bin/edzsplit

# Cách 2: Docker
docker build -t edzsplit .
docker run --rm -v /data:/data edzsplit inspect /data/EKF.edz
```

### Windows

Cần [.NET 8 runtime](https://dotnet.microsoft.com/) và [7-Zip](https://www.7-zip.org/) (`7z.exe` hoặc trỏ biến môi trường `EDZSPLIT_7Z` vào nó). Hoặc build từ source bằng .NET 8 SDK.

## Sử dụng

```bash
# Kiểm tra (container, manifest, số package/item, thống kê repair ref)
edzsplit inspect EKF.edz
edzsplit inspect EKF.edz --json > ekf-inspect.json

# Tách toàn bộ, closure full (mặc định)
edzsplit split EKF.edz --out out/ekf --log ekf.jsonl

# Tách một số part cụ thể
edzsplit split EKF.edz --out out/ekf --only EKF.ILR-10DR-230A,EKF.10201

# Chạy tiếp sau khi gián đoạn
edzsplit split EKF.edz --out out/ekf --resume

# Dry-run (chỉ lập kế hoạch)
edzsplit split EKF.edz --out out/ekf --dry-run

# Case A: archive chứa sẵn các EDZ con
edzsplit case-a Theben.7z --out out/theben

# Verify một EDZ bất kỳ (cấu trúc)
edzsplit verify out/ekf/EKF.ILR-10DR-230A.edz

# Smoke test (không cần EPLAN, không cần file ngoài)
edzsplit selftest
```

Exit code: `0` ok · `2` tham số sai · `3` xong nhưng có warning/quarantine · `4` thất bại.
Mọi lệnh in JSONL events ra stdout.

## Kiểm chứng trước khi tin

Kiểm chứng cấu trúc ≠ chứng minh import vào EPLAN. Trước khi dùng kết quả cho sản xuất, chạy import oracle trên một **Parts Database scratch**:

1. `tools/EplanEdzValidator/` build ra console .NET Framework chỉ dùng cho test, tạo/mở/import vào parts database **scratch** qua EPLAN API chính thức rồi assert part tồn tại sau import.
2. Xem [docs/EVIDENCE.md](docs/EVIDENCE.md#import-oracle) để biết các bước và ranh giới những gì đã/chưa được kiểm chứng.

**Đừng trỏ oracle vào parts database đang sản xuất.**

## Bố cục repo

```
src/edzsplit/            Tool tách EDZ (.NET 8, không package bên thứ ba)
tools/EplanEdzValidator/ Oracle EPLAN API chỉ dùng test (.NET Framework 4.8, Windows + EPLAN cài sẵn)
docs/                    Báo cáo (VI/EN), evidence, hướng dẫn build Linux
Dockerfile               Image Ubuntu 24.04 (p7zip + .NET 8 runtime)
```

## Giới hạn hiện tại

Xem [docs/REPORT.vi.md §10](docs/REPORT.vi.md#10-giới-hạn-và-những-điều-chưa-làm). Điểm chính: writer ghi block 7z không nén (lớn hơn ~2×); tách tuần tự; mới kiểm chứng tương thích ngữ nghĩa file EPLAN 2022; import UAT còn chờ môi trường EPLAN phù hợp; chưa chạy thật trên Linux (mới cross-compile).

## Đóng góp

Xem [CONTRIBUTING.md](CONTRIBUTING.md). Vui lòng không kèm parts database có license hoặc file EDZ của hãng trong issue/PR.

## License

MIT — xem [LICENSE](LICENSE).
