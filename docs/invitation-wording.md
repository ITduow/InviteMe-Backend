# Preset lời mời theo wedding

Mỗi wedding có một bộ preset riêng. Một preset áp dụng cho nhiều danh xưng; danh xưng chưa được gán hoặc để trống dùng preset mặc định. Mỗi bộ phải có đúng một preset mặc định, mỗi danh xưng chỉ được gán một lần (không phân biệt hoa thường, khoảng trắng thừa và Unicode tổ hợp).

Các biến được hỗ trợ trong nội dung văn bản:

| Biến | Giá trị |
| --- | --- |
| `{{recipientName}}` | Tên đầu mối nhận thiệp từ `PlanningGuest` |
| `{{recipientLabel}}` | Kiểu lời mời + danh xưng + tên, ví dụ `gia đình bác Nguyễn Văn An` |
| `{{honorific}}` | Danh xưng của khách |
| `{{senderPronoun}}` | Cách người mời tự xưng, cấu hình trên preset |

Nội dung phải có `recipientLabel` hoặc `recipientName`. Nếu dùng `senderPronoun`, cần điền cách tự xưng. Các biến không được hỗ trợ và dấu ngoặc mẫu sai bị từ chối. Giá trị của khách được thay một lần, không được diễn giải lại như mã template.

## API

Base path: `/api/weddings/{weddingId}/invitation-wording` (JWT required).

- `GET`: trả `{ weddingId, version, presets }`. Cần `INVITATION_SEND`. Chưa có cấu hình lưu thì trả preset gợi ý với `version = 0`, không ghi DB.
- `PUT`: nhận `{ expectedVersion, presets }`, cần `WEDDING_EDIT`. Lưu toàn bộ bộ preset trong một transaction, khóa wedding và kiểm tra version. Trả `409` khi bản chỉnh sửa đã cũ. Cấu hình sai trả `400`.
- `POST /preview`: nhận `{ guestId, presets? }`, cần cả `INVITATION_SEND` và `GUEST_VIEW`. Có `presets` thì xem trước bản đang chỉnh, không lưu; bỏ qua trường này thì dùng cấu hình hiện hành. Khách phải thuộc wedding trong URL, nếu không trả `404`.

Preset có các trường `id`, `name`, `content`, `senderPronoun`, `isDefault`, `honorifics`. Tối đa 30 preset, nội dung 2000 ký tự và tối đa 50 danh xưng mỗi preset.

## FE

Mở **Wedding → Invitations → Mở Edit Studio · Lời mời**, hoặc **Website → Mở Edit Studio**. Studio theo wedding dùng route `/weddings/{weddingId}/studio`; mục **Lời mời** chứa preset, gán danh xưng và chọn khách xem trước. Preset vẫn được lưu theo wedding qua API trên. Chỉ bấm **Lưu cấu hình** mới lưu thay đổi.

Nút **Thêm khối lời mời vào thiệp** tạo một `StudioText` có `textBinding = "invitationWording"`. Cũng có thể gán liên kết này cho một khối chữ có sẵn ở Thuộc tính. Nội dung render từ kết quả preview BE, giữ nguyên font/màu/vị trí. Giá trị của khách chỉ ở React context; export document giữ binding, không ghi tên hoặc lời mời đã render của khách. Nếu bỏ chọn khách hoặc thay đổi preset mà chưa preview lại, canvas dùng câu mẫu trung tính.

Studio không có wedding (mở từ thư viện mẫu công khai) vẫn hoạt động như trước; mục Lời mời hướng dẫn mở Studio từ wedding để dùng dữ liệu khách thật.

Migration: `AddInvitationWordingSettings`, tạo bảng `invitation_wording_settings` gồm một bản ghi JSONB có version cho mỗi wedding. Token khách không được thay đổi hoặc trả về qua các API preset. Mở link công khai vẫn là bước tích hợp riêng.
