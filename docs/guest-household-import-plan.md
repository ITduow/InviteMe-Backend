# Đề xuất cải thiện import khách theo gia đình

Trạng thái: đã triển khai import gia đình v2 và kiểm chứng flow qua tests ngày 11/10/2026.
Xem `guest-file-import.md` cho contract đang chạy. FE dashboard và sửa preview đã triển khai ở nhánh feat/import-dashboard của repository frontend. RSVP tên tùy chọn vẫn chưa triển khai.

## 1. Kết luận sau khi kiểm tra code

Giữ mô hình hiện có: một `WeddingGuest` là đầu mối nhận thiệp; mỗi người thuộc thiệp là một `GuestParticipant`, bao gồm người đại diện. Không cần tạo bảng Family cho phạm vi này.

- JSON import đã tạo PRIMARY và các Companions: `GuestImportStore.cs`.
- CSV/XLSX v2 gom nhóm, tạo một WeddingGuest + N participants; v1 vẫn tạo một khách cho mỗi dòng.
- `InvitationStore.CreateAsync` chỉ cho một Invitation trên một Guest trong wedding. Import không tự tạo/gửi Invitation.
- RSVP lấy toàn bộ participants theo Invitation.GuestId và cho chọn từng ID: `RsvpStore.cs`.
- Xếp chỗ dùng ParticipantId, chỉ cho ATTENDING: `SeatingStore.cs`.
- DB đã có unique index bảo đảm tối đa một PRIMARY trên một Guest. Import phải bảo đảm có đúng một PRIMARY.
- GuestGroup là nhóm tổ chức (bạn học, đồng nghiệp), không dùng để gom thiệp.

Các giới hạn cần hiểu: người đại diện có thể không dự nhưng vẫn xác nhận cho thành viên khác; gia đình không bắt buộc ngồi cùng bàn; hệ thống hiện đưa cả nhóm người được chọn vào waitlist nếu không đủ sức chứa, không tự nhận một phần.

## 2. Excel đề xuất

Đổi tên “Mã thiệp” thành **Mã nhóm mời** để tránh hiểu nhầm đó là InvitationId/token. Đây là mã nhập liệu người tổ chức đặt, ví dụ GD01; không đưa token bí mật vào Excel.

| Mã nhóm mời | Họ và tên | Vai trò | Số điện thoại | Email | Phía khách mời | Mối quan hệ | Nhóm khách | Số người được thêm | Ghi chú |
|---|---|---|---|---|---|---|---|---|---|
| GD01 | Nguyễn Văn An | Người đại diện | 0912345678 | | Nhà trai | Họ hàng | Họ hàng nhà trai | 0 | Gia đình An |
| GD01 | Trần Thị Bình | Vợ/chồng | | | | | | | |
| GD01 | Nguyễn Văn Minh | Con | | | | | | | |
| GD01 | Nguyễn Văn Mai | Con | | | | | | | |

Một người đi một mình: một dòng, vai trò Người đại diện; mã có thể bỏ trống. Mã trống chỉ được dùng cho dòng đại diện và được tạo riêng, không gom tất cả dòng trống thành một nhóm.

Vai trò cố định: Người đại diện/PRIMARY, Vợ/chồng/SPOUSE, Con/CHILD, Thành viên khác/OTHER. Không ép gia đình có tối đa hai người lớn. Vai trò mô tả quan hệ trong nhóm; “Mối quan hệ” mô tả người đại diện với chủ tiệc, là hai ý khác nhau.

Thông tin liên hệ, Side, Relationship, Nhóm khách, quyền thêm và Ghi chú chỉ nhập ở dòng đại diện. Thành viên có giá trị ở các cột này: báo lỗi rõ ràng, không âm thầm bỏ dữ liệu. DietaryNote từng người nằm ngoài format v1.

Mã trim, không phân biệt hoa thường, dài tối đa 50, chỉ chữ ASCII/số/_/-. Các dòng cùng mã không cần nằm cạnh nhau; dòng đại diện không cần đứng đầu. Tên tối đa 150, số điện thoại dạng text. Side/Relationship dùng danh mục hiện có, trống giữ null, sai báo lỗi.

## 3. Số người được thêm tính thế nào?

Đây là quyền mời thêm ngoài những người đã có tên, mapping vào MaxPlusOne, không phải tổng số thành viên.

| Dữ liệu | Người có tên | Được thêm | Tối đa có thể dự |
|---|---:|---:|---:|
| An, Bình, Minh, Mai | 4 | 0 | 4 |
| Chỉ biết An | 1 | 3 | 4 |
| Biết An và Bình | 2 | 2 | 4 |

Không tạo participant cho các suất được phép thêm lúc import. Các participant đã có tên bắt đầu PENDING, chưa chiếm sức chứa hay ghế. Số thực sự dự tính từ ATTENDING, không dùng ExpectedCompanionCount.

Bỏ cột “Số người đi kèm dự kiến” trong format mới. Giữ field DB để tương thích JSON/v1 và báo cáo hiện có: v2 tự điền bằng số thành viên có tên ngoài PRIMARY, không yêu cầu người dùng nhập.

## 4. Flow và data cụ thể

1. Upload file → đọc các dòng, gom GD01, kiểm tra đủ một đại diện.
2. Preview hiện “Gia đình An — 4 người có tên — được thêm 0”, kèm toàn bộ thành viên và lỗi theo dòng/cột.
3. Người tổ chức chọn cả nhóm GD01 và confirm.
4. Một transaction tạo G01 và P01–P04, session result và audit. Không tạo/gửi thiệp, không RSVP tự động.
5. Người tổ chức tạo Invitation I01 cho G01 rồi gửi link cho An bằng luồng thiệp hiện có. Không có liên hệ vẫn import được; phải chọn kênh/liên hệ hợp lệ lúc gửi hoặc chia sẻ link thủ công.
6. An mở link, chọn P01/P02/P03 tham dự, P04 không dự.
7. Đủ sức chứa: P01–P03 ATTENDING, P04 DECLINED; RSVP confirmedPartySize = 3. Thiếu sức chứa: cả ba WAITLISTED theo logic hiện tại.
8. Xếp ghế riêng cho P01/P02/P03. Đổi tên sau này giữ nguyên ID, không tạo lại người làm mất liên kết ghế.

| Entity | Data sau import |
|---|---|
| WeddingGuest | G01; GuestCode GD01; FullName An; Phone 0912345678; MaxPlusOne 0 |
| GuestParticipant | P01/G01/An/PRIMARY/PENDING |
| GuestParticipant | P02/G01/Bình/SPOUSE/PENDING |
| GuestParticipant | P03/G01/Minh/CHILD/PENDING |
| GuestParticipant | P04/G01/Mai/CHILD/PENDING |
| GuestImportSession | Snapshot preview + lựa chọn + kết quả, phục vụ retry |

An ở hai bảng phục vụ hai việc: liên hệ và tham dự. Đếm người bằng GuestParticipant, không cộng số WeddingGuest với số participants. Khi có chức năng đổi tên đại diện phải đồng bộ tên WeddingGuest và PRIMARY trong cùng transaction.

## 5. Quy tắc import để tránh tạo sai dữ liệu

- Một mã phải có đúng một PRIMARY. Thiếu hoặc có hai đại diện: cả nhóm không được confirm.
- Một thành viên lỗi: cả nhóm lỗi. Có thể chọn nhóm khác hợp lệ; không nhập một phần gia đình.
- Confirm đổi từ chọn rowNumbers rời rạc sang chọn groupKeys do preview cấp; BE lấy nguyên nhóm từ snapshot, không tin số người FE gửi.
- Preview trả source row numbers, groupKey, mã, đại diện, danh sách members, namedPersonCount, additionalAllowance, maximumPartySize, lỗi/cảnh báo. Result phân biệt importedInvitationUnitCount và importedParticipantCount, kèm GuestId/ParticipantIds.
- Giữ giới hạn 500 dòng dữ liệu, 5 MB và bảo vệ XLSX hiện có. Giới hạn v1 tối đa 20 thành viên ngoài PRIMARY và tối đa 20 suất thêm, tương thích giới hạn RSVP hiện có 41 người.
- Có mã: dùng GuestCode (DB hiện có), unique trong wedding. Mã đã tồn tại: báo conflict, không tự append/merge/update gia đình cũ. Cho phép sửa file dùng mã mới nếu thực sự là nhóm khác.
- Mã trống của khách lẻ: BE sinh GuestCode và giữ cố định trong session. Retry confirm cùng lựa chọn trả cùng kết quả.
- Kiểm tra nghi trùng đại diện bằng tên + liên hệ hiện có. Không coi trùng họ hoặc chung số điện thoại là đủ để gộp. Tên thành viên giống nhau chỉ cảnh báo vì không phải định danh.
- Lặp nguyên một dòng: cảnh báo, không tự xóa. Mã trùng là cách gom nhóm trong file, không phải cảnh báo trùng khách cho mỗi dòng.
- Confirm kiểm tra lại quyền, expiry, mã/duplicate dưới wedding lock; tất cả nhóm được chọn thành công cùng transaction hoặc rollback toàn bộ.
- Format mới phải có version rõ ràng; không suy đoán rồi biến template cũ mỗi dòng một người thành gia đình. FE chuyển template sau khi BE hỗ trợ. Session cũ cần codec/version để confirm đúng snapshot hoặc báo tải lại rõ ràng.

## 6. Người đi kèm chưa biết tên: phần mở rộng riêng

Hiện RSVP nhận PlusOnes là danh sách tên bắt buộc và tạo record mới. Chưa hỗ trợ chỉ nhập số người hay đổi tên participant.

Nếu triển khai tên tùy chọn: thêm trạng thái tên thực/unknown ở participant, trả displayName riêng. Chữ “Đi cùng An #1” chỉ là nhãn giao diện, không ghi thành tên thật. Có thể dùng FullName nullable + NameStatus KNOWN/UNKNOWN, migration và DTO phải thay đổi đồng bộ; PRIMARY và thành viên nhập Excel vẫn bắt buộc tên.

RSVP thêm hai người chưa tên → tạo hai PLUS_ONE có ID ổn định. Gửi lại phản hồi sử dụng ID đã trả về; chỉ tạo người mới cho yêu cầu thêm mới. Update tên trên cùng ID. Không định danh bằng tên, không mặc định người trùng tên là một người.

Quota tính số PLUS_ONE được chọn, gồm cũ và mới, tối đa MaxPlusOne. Người được thêm trước đó nhưng nay không đi giữ ID với DECLINED; ưu tiên dùng lại record này khi chọn lại để tránh tích lũy record mới qua mỗi lần RSVP. Request stale version bị từ chối; retry không tạo thêm người. Cần giới hạn lượng record tồn tại và endpoint chỉnh tên có kiểm tra token/quyền/version.

Người chưa tên vẫn cần ATTENDING để được xếp ghế. Không giảm tham dự người đã có ghế/check-in: logic hiện tại chặn và yêu cầu gỡ ghế trước; giữ quy tắc này. Sửa tên không được thay đổi ghế/attendance.

## 7. Tasks theo thứ tự

Đã làm: contract v2/header/roles, grouped preview/confirm/result, session version,
parser/validation theo nhóm, lưu nguyên gia đình, kiểm tra mã/trùng, retry/concurrency,
unit tests và integration flow import → thiệp → RSVP → ghế. Checklist chi tiết dưới đây
giữ để theo dõi cả phần kiểm chứng tiếp và các mở rộng. T5/T6 chưa làm.

### T1 — Chốt contract format gia đình
- [x] Thêm version mới, header/mapping và labels vai trò.
- [x] Bỏ cột dự kiến khỏi version mới; giữ compatibility version cũ có chủ đích.
- [x] Định nghĩa grouped preview/confirm/result và xử lý session version.
- [x] Ghi rõ validation, max người và mã unique; cập nhật tài liệu API.

### T2 — Parser và validation theo nhóm
- [x] Gom dòng theo mã, hỗ trợ mã trống khách lẻ, các dòng không liên tiếp.
- [x] Validate đúng một PRIMARY, metadata chỉ ở đại diện, báo lỗi toàn nhóm.
- [x] Duplicate warnings theo đại diện/thành viên, không coi mọi dòng chung mã là duplicate.
- [x] Unit tests: gia đình 4; khách lẻ; dòng đảo thứ tự; thiếu/hai PRIMARY; role sai; member lỗi; mã normalization; quota và giới hạn; CSV/XLSX; format cũ.

### T3 — Confirm lưu gia đình
- [x] Tạo một WeddingGuest + N participants; tái dùng GuestCode, không thêm bảng Family.
- [x] Chọn nhóm nguyên vẹn, unique mã theo wedding, transaction/audit/idempotency.
- [x] Kết quả trả số nhóm và số người riêng biệt.
- [x] Integration tests: import 4 dòng → 1 guest/4 participants/0 invitation; preview không tạo guest; nhóm lỗi không import; retry/concurrent confirm không nhân đôi; mã đã tồn tại; quyền/expiry; rollback khi conflict sau preview.

### T4 — Kiểm chứng flow thiệp → RSVP → seating
- [x] Test tạo một thiệp cho G01; link thấy đủ 4 participants.
- [x] Test chọn 3 người → confirmed 3 → xếp 3 ghế; người đại diện không đi vẫn chọn được người khác.
- [x] Test không đủ capacity → waitlist cả nhóm; participant không thuộc thiệp bị từ chối.
- [x] Test không đổi attendance người đã xếp ghế/check-in trái quy tắc (kết hợp test flow gia đình và workflow hiện có).
- [ ] Kiểm tra các màn/API thống kê: số thiệp, số người được mời, số ATTENDING và số ghế không đếm lẫn nhau.

### T5 — RSVP thêm người chưa biết tên
- [ ] Migration tên unknown, DTO displayName và API update tên giữ ID.
- [ ] Cải thiện thêm/chọn lại PLUS_ONE và chống tạo record trùng khi gửi lại.
- [ ] Tests quota cũ+mới, 0 quota, optional name, rename giữ ghế, stale version, decline/chọn lại, concurrency và quyền token.

### T6 — FE phối hợp sau khi BE sẵn sàng
- [ ] FE giữ template XLSX/dropdown; CSV vẫn được BE validate.
- [ ] Preview theo nhóm, chọn cả nhóm, hiển thị người đã có tên/quyền thêm/tổng tối đa riêng.
- [ ] RSVP chọn từng thành viên; optional names chỉ mở khi T5 hoàn tất.
- [ ] Seating hiển thị tên hoặc nhãn unknown, kéo theo ParticipantId.

Ưu tiên T1–T4 để hoàn thiện import gia đình đã biết tên. T5 là thay đổi RSVP/schema riêng, không cần gộp để làm được gia đình 4 người trong ví dụ. T6 chưa triển khai FE trong branch hiện tại.
