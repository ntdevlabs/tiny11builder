# GitHub Actions Workflow - Build Tiny11 ISO

Workflow này tự động hóa quá trình tạo Windows 11 tiny image bằng GitHub Actions.

## Cách sử dụng

1. **Chuẩn bị Windows 11 ISO URL**
   - Bạn cần có link download Windows 11 ISO (ví dụ: từ Microsoft hoặc các nguồn khác)
   - Link phải hỗ trợ direct download

2. **Chạy workflow**
   - Vào tab **Actions** trong repository
   - Chọn workflow **Build Tiny11 ISO**
   - Click **Run workflow**
   - Điền các thông tin:
     - **iso_url**: Link download Windows 11 ISO
     - **script_type**: Chọn `tiny11maker` hoặc `tiny11Coremaker`
     - **enable_dotnet35**: Bật/tắt .NET 3.5 (chỉ cho Core)
     - **scratch_drive**: Drive letter cho scratch disk (để trống sẽ dùng script root)

3. **Chờ quá trình build**
   - Workflow sẽ tự động:
     - Download Windows ISO
     - Mount ISO
     - Chạy script build
     - Tạo ISO file
     - Upload artifact

4. **Download ISO**
   - Sau khi build xong, vào tab **Actions**
   - Click vào run vừa hoàn thành
   - Scroll xuống phần **Artifacts**
   - Download file `tiny11-iso`

## Lưu ý

- Workflow chạy trên Windows runner và có thể mất 60-180 phút
- Runner cần có đủ dung lượng để xử lý ISO (khuyến nghị ít nhất 50GB free space)
- Script sẽ tự động trả lời các prompts trong quá trình build
- ISO sẽ được giữ trong 7 ngày sau khi upload

## Script Types

### tiny11maker
- Script chính, giữ lại khả năng service (có thể thêm language, update sau)
- Khuyến nghị cho sử dụng thông thường

### tiny11Coremaker  
- Script Core, loại bỏ nhiều thành phần hơn
- Không thể service sau khi tạo (không thể thêm language, update)
- Chỉ khuyến nghị cho testing/development

## Troubleshooting

- Nếu workflow fail ở bước mount ISO: Kiểm tra lại link download có hợp lệ không
- Nếu không tìm thấy ISO sau build: Kiểm tra logs để xem script có chạy thành công không
- Nếu hết thời gian: Tăng timeout trong workflow (mặc định 180 phút)

