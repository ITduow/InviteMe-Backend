# Import khách mời bằng CSV/Excel

API hỗ trợ format gia đình **v2** bên dưới và format cũ **v1** để giữ tương thích.
Logic thiết kế và các task còn lại nằm trong [kế hoạch import gia đình](guest-household-import-plan.md).

## Format v2: mỗi người một dòng, một nhóm nhận chung thiệp

FE giữ template với đúng 10 header bên dưới (thứ tự có thể đổi). BE nhận ra version
qua bộ header chính xác; preview/session có `formatVersion = 2`, không suy đoán từ tên người.

| Header | Dòng đại diện | Dòng thành viên |
|---|---|---|
| Mã nhóm mời | Bắt buộc khi có thành viên; khách lẻ được trống | Bắt buộc, cùng mã đại diện |
| Họ và tên | Bắt buộc, tối đa 150 ký tự | Bắt buộc, tối đa 150 ký tự |
| Vai trò | Người đại diện/PRIMARY | Vợ/chồng/SPOUSE, Con/CHILD, Thành viên khác/OTHER |
| Số điện thoại | Tùy chọn, text | Phải để trống |
| Email | Tùy chọn | Phải để trống |
| Phía khách mời | Tùy chọn, danh mục cố định | Phải để trống |
| Mối quan hệ | Tùy chọn, danh mục cố định | Phải để trống |
| Nhóm khách | Tùy chọn | Phải để trống |
| Số người được thêm | Tùy chọn, trống = 0, số nguyên 0–20 | Phải để trống, kể cả số 0 |
| Ghi chú | Tùy chọn | Phải để trống |

Mã nhóm trim, chuẩn hóa hoa, tối đa 50 ký tự ASCII chữ/số/_/-. Mã trống của khách lẻ
có groupKey riêng `ROW:<rowNumber>`. Mỗi nhóm đúng một đại diện, tối đa 20 thành viên
ngoài đại diện; dòng không cần liền nhau hay đại diện đứng trước. Dòng trống hoàn toàn bỏ qua.
Không cho tên trống: người chưa biết tên chỉ khai bằng quyền thêm trên dòng đại diện.
Giới hạn 500 dòng dữ liệu không trống/5 MB, công thức bị từ chối như v1.

Ví dụ CSV hợp lệ (gia đình 4 người, chưa có liên hệ):

```csv
Mã nhóm mời,Họ và tên,Vai trò,Số điện thoại,Email,Phía khách mời,Mối quan hệ,Nhóm khách,Số người được thêm,Ghi chú
GD01,Nguyễn Văn An,Người đại diện,,,,,,,
GD01,Trần Thị Bình,Vợ/chồng,,,,,,,
GD01,Nguyễn Văn Minh,Con,,,,,,,
GD01,Nguyễn Văn Mai,Con,,,,,,,
```

Preview vẫn trả rows/errors/warnings, bổ sung `groups`: mỗi nhóm có groupKey, code,
members, namedPersonCount, additionalAllowance, maximumPartySize. Ví dụ này là 4 + 0 = 4.
Issue theo rowNumber/column; một dòng lỗi khiến cả nhóm không confirm được.

Confirm v2 chọn **nguyên nhóm**, không gửi rowNumbers:

```json
{
  "importId": "82e70388-4ff0-4cf3-a37e-7130319712d6",
  "groupKeys": ["GD01"],
  "acknowledgeDuplicates": false
}
```

Kết quả `importedCount = 1` là số đầu mối nhận thiệp, `importedParticipantCount = 4`
là số người. `guests` trả GuestId/GuestCode và danh sách participants với ID riêng.
Lưu một WeddingGuest + bốn GuestParticipant PENDING, chưa tạo thiệp/RSVP/ghế.
Side/Relationship trống giữ null; liên hệ trống vẫn import được.

Mã có sẵn được lưu vào GuestCode, unique trong wedding. Mã đã tồn tại báo lỗi ở preview;
confirm kiểm tra lại và trả 409 nếu mã xuất hiện sau preview. Không append/merge/update người cũ.
Cảnh báo trùng tên thành viên không tự gộp. Retry cùng session/cùng nhóm không tạo thêm người;
session v1 cũ vẫn confirm bằng rowNumbers. Quota thêm không tạo participant trước RSVP.

Không còn cột dự kiến trong v2. Field ExpectedCompanionCount trong DB được điền bằng số
thành viên đã có tên ngoài đại diện để báo cáo EstimatedHeadcount hiện hữu đếm đủ người;
không lấy quota thêm làm dự kiến. Số thực sự dự vẫn đếm ATTENDING.
RSVP thêm người hiện vẫn yêu cầu tên; tên tùy chọn là task riêng chưa triển khai.

## API cho frontend

Tất cả endpoint bên dưới yêu cầu JWT và quyền `GUEST_EDIT` trên đám cưới.
Root: `/api/weddings/{weddingId}/guests`.

| Method | Route | Mục đích |
|---|---|---|
| GET | `/import-options` | Danh mục `{ code, label }` cho side/relationship |
| POST | `/import/preview` | Multipart form với đúng một file CSV/XLSX, field `file` |
| POST | `/import/confirm` | Xác nhận các dòng đã kiểm tra |

API JSON cũ `POST /guests/import` và `POST /guests` vẫn hoạt động.
Không gửi thiệp hoặc thay đổi RSVP khi import.

## Format v1: template cũ và mapping

| Header | Thuộc tính lưu | Quy tắc |
|---|---|---|
| Họ và tên | FullName | Bắt buộc; trim; tối đa 150 ký tự |
| Số điện thoại | Phone | Text; tối đa 30 ký tự; 7–15 chữ số, cho phép +, khoảng trắng, -, (, ) |
| Email | Email | Tùy chọn; tối đa 255 ký tự; địa chỉ email thuần |
| Phía khách mời | Side | Nhà gái/BRIDE, Nhà trai/GROOM, Cả hai/MUTUAL, Khác/OTHER |
| Mối quan hệ | Relationship | Họ hàng/FAMILY, Bạn bè/FRIEND, Đồng nghiệp/COLLEAGUE, Hàng xóm/NEIGHBOR, Đối tác/PARTNER, Khác/OTHER |
| Nhóm khách | GroupId qua GuestGroup.Name | Tùy chọn; tối đa 150 ký tự; tìm/tạo trong đúng wedding |
| Số người đi kèm tối đa | MaxPlusOne | Số nguyên 0–20, trống = 0; AllowedPlusOne = giá trị > 0 |
| Số người đi kèm dự kiến | ExpectedCompanionCount | Số nguyên 0–20, trống = 0; không gồm khách chính |
| Ghi chú | Notes | Tùy chọn; tối đa 2000 ký tự trong file import |

Hai danh mục cố định được biểu diễn bằng enum C# (`GuestSide`, `GuestRelationship`);
entity/DB vẫn lưu string code để tương thích schema và API hiện hữu.
Trống side/relationship = `null` (chưa phân loại). Không tự chuyển giá trị sai thành OTHER.
Group là nhãn tổ chức; không có tác dụng cho phép RSVP thay cả nhóm.

FE giữ và cung cấp template; BE không sinh file hoặc có endpoint tải template.
Template FE cần giữ đủ 9 header chính xác, có thể thay đổi thứ tự; nên định dạng điện thoại dạng text.
CSV dùng UTF-8 (có thể có BOM). API `/import-options` cung cấp danh mục code/label cho FE.
Mỗi dòng là một khách chính; tối đa 500 dòng và 5 MB.
XLSX ưu tiên sheet `KhachMoi`, nếu không có thì đọc sheet đầu tiên.
Không chấp nhận công thức trong sheet dữ liệu, XLS, XLSM hoặc file đổi đuôi giả.
Giới hạn tổng dung lượng ZIP giải nén 30 MB và 1000 entries.

## Preview và lỗi

Preview trả `{ importId, expiresAt, rows, errors, warnings }`.
Issue có `{ rowNumber, column, code, message }`; rowNumber bao gồm hàng header (data bắt đầu ở 2).
Lỗi dữ liệu từng dòng được trả trong response 200 để FE hiển thị/sửa;
lỗi toàn file/header trả ProblemDetails 422. Preview không tạo guest/group/participant.
FE nên hiển thị toàn bộ rows với lỗi, cho chọn các dòng hợp lệ và hỏi xác nhận nếu có cảnh báo trùng.

Ví dụ confirm:

```json
{
  "importId": "82e70388-4ff0-4cf3-a37e-7130319712d6",
  "rowNumbers": [2, 4, 5],
  "acknowledgeDuplicates": false
}
```

RowNumbers phải chứa 1–500 dòng khác nhau, tồn tại trong preview, không có lỗi.
Dòng không chọn sẽ không import. Mỗi preview chỉ confirm một lần; sửa lựa chọn sau confirm trả 409.
Gửi lại cùng importId và cùng tập rowNumbers trả lại kết quả cũ, không tạo thêm khách.
Session tồn tại trong database, gắn với actor + wedding, hết hạn sau 30 phút;
request mất quyền GUEST_EDIT sẽ bị chặn kể cả đã preview.
Tối đa 10 preview chưa confirm còn hạn/actor/wedding.
Session hết hạn của wedding được dọn khi tạo preview mới trong wedding đó.

Nghi trùng khi cùng tên và cùng phone/email, hoặc cùng tên mà cả hai không có liên hệ.
So tên/email không phân biệt hoa thường; phone bỏ ký tự phân cách và chuẩn hóa +84/0.
Không tự gộp hoặc cập nhật guest cũ. Confirm kiểm tra lại guest hiện tại dưới wedding lock.
Có nghi trùng mà chưa acknowledge: 409 `IMPORT_DUPLICATES_REQUIRE_ACKNOWLEDGEMENT`.
`acknowledgeDuplicates=true` cho phép tạo record riêng, không merge.
Estimate vượt max đi kèm chỉ cảnh báo vì đây là dự đoán, không phải quota.

Confirm tạo nhóm còn thiếu (so tên không phân biệt hoa thường), guest code G + UUID,
participant PRIMARY, audit GUEST_IMPORTED trong cùng transaction.
Kết quả `{ importId, importedCount, guestIds }`.

## Migration

`AddGuestFileImport` tạo bảng session, bỏ NOT NULL/default MUTUAL của guests.side,
và thêm CHECK cho relationship. Side cũ MUTUAL giữ nguyên vì không xác định được người dùng đã chọn hay mặc định.
Relationship cũ ngoài danh mục chuyển sang Notes với nhãn “Quan hệ trước chuyển đổi” và đặt relationship NULL;
không đoán loại quan hệ từ free text.

Chạy migration vào database đích trước khi triển khai API:

```powershell
dotnet ef database update --project src/InviteMe.Infrastructure
```

Script `GuestFileImportSupport.sql` là phần bổ sung tương đương khi so sánh baseline.

## Kiểm tra

```powershell
dotnet test InviteMe.sln
# Test PostgreSQL dùng database tạm riêng do harness tạo và xóa:
$env:INVITEME_TEST_CONNECTION_STRING = '<test PostgreSQL connection string>'
dotnet test InviteMe.sln
```

Các test bao phủ Unicode, phone số 0 đầu, đọc XLSX, enum sai, công thức,
header/size/row limits, kiểm tra quyền, preview không tạo guest, chọn dòng hợp lệ,
nhóm, confirm đồng thời/idempotency, duplicate ở preview và sau preview, expiry và model/migration.

## API đọc chi tiết nhóm cho dashboard

`GET /api/weddings/{weddingId}/guests/{guestId}` yêu cầu `GUEST_VIEW`. Trả đại diện, mã nhóm, phone/email/side/relationship/nhóm khách/notes, additionalAllowance và participants có ID, vai trò, trạng thái RSVP riêng. Đọc trong snapshot nhất quán; guest phải thuộc đúng wedding. Không có guest trả 404; không có quyền trả 403. Không đổi danh sách picker hiện hữu, không trả ghế hay tạo/gửi thiệp.

## Sửa trước khi import
Preview bổ sung rawValues (giá trị gốc theo header), giữ cả ô sai enum/số. FE sửa nhóm trong drawer rồi dựng CSV và gọi lại preview để BE kiểm tra toàn bộ danh sách. Confirm vẫn chỉ nhận importId/groupKeys của phiên đã được BE kiểm tra. Sửa không ghi khách và không thay đổi Excel gốc. File sai cấu trúc cần sửa file gốc. Multipart preview nhận replacesImportId khi sửa: thay phiên chưa confirm của đúng người dùng/đám cưới trong cùng transaction. Phiên cũ không còn confirm được; sửa nhiều lần không tăng số phiên đang mở. Không thay thế phiên đã confirm/hết hạn. Lỗi đọc file giữ nguyên phiên trước.
