# Ru Roadmap

Mục tiêu dài hạn: Ru giao tiếp tự nhiên bằng tiếng Việt và tiếng Anh; sau đó có thể nghe, nhìn và điều khiển avatar như một VTuber.

## Nền tảng hội thoại

- [x] Hoàn tất base training và đánh giá checkpoint tốt nhất.
- [x] Tạo và lọc bộ dữ liệu hội thoại nguồn mở Việt-Anh có kèm nguồn/giấy phép.
- [x] Build and run the V2 response-only fine-tune pipeline (quality gate failed; do not use it as Ru's final backend).
- [x] Build and audit the local V3 authored conversation corpus (120 Vietnamese-English examples, 30 contextual).
- [x] Run the first V3 QLoRA adapter (quality gate failed; do not use it as Ru's final backend).
- [x] Build, train, and evaluate the local V4 Qwen 1.5B candidate (quality gate failed; do not use it as Ru's final backend).
- [x] Evaluate the local V5 Qwen 3B base (hardware gate passed at 2,034.184 MiB; conversation gate failed on four Vietnamese anh/em voice errors; do not fine-tune it).
- [x] Evaluate local Qwen3 4B in non-thinking mode (hardware gate passed at 2,635.017 MiB; manual bilingual conversation score 11/32; do not fine-tune it).
- [x] Run the Qwen3 4B language-specific prompt-routing smoke (automatic 8/8 and English 4/4 passed at 2,628.293 MiB; manual score 5/8 with a role reversal and Vietnamese-only mixed replies; do not continue Qwen3).
- [ ] Make Ru consistently natural in short Vietnamese-English conversations.
- [ ] Thu thập các lượt chat được đánh dấu hữu ích.
- [ ] Fine-tune hội thoại từ `data/conversations/approved.jsonl`.
- [ ] Tạo bộ câu hỏi đánh giá giao tiếp Việt-Anh và theo dõi chất lượng sau mỗi lần fine-tune.

## Giọng nói

- [ ] Thu âm từ microphone trong GUI.
- [ ] Speech-to-text cục bộ để đổi giọng nói thành văn bản.
- [ ] Cho Ru trả lời từ nội dung đã nhận dạng.
- [ ] Text-to-speech tiếng Việt và tiếng Anh để đọc câu trả lời.
- [ ] Điều chỉnh giọng đọc, tốc độ, âm lượng và nút bật/tắt microphone.
- [ ] Đo độ trễ từ lúc nói đến lúc Ru phản hồi.

## Vision

- [ ] Cho phép chọn ảnh hoặc lấy khung hình từ webcam.
- [ ] Dùng model vision có sẵn để tạo mô tả ảnh hoặc nhận diện đối tượng.
- [ ] Đưa phần mô tả ảnh vào ngữ cảnh text của Ru trước khi trả lời.
- [ ] Hiển thị rõ ảnh nào đang được dùng và cho phép xoá ảnh khỏi phiên chat.
- [ ] Đánh giá riêng câu hỏi về ảnh bằng tiếng Việt và tiếng Anh.

## Avatar 2D / 3D kiểu VTuber

- [ ] Chọn chuẩn avatar 2D (Live2D) hoặc 3D (VRM) cho phiên bản đầu tiên.
- [ ] Kết nối Ru với ứng dụng render avatar qua API/OSC/VMC tương ứng.
- [ ] Đồng bộ trạng thái nói của Ru với chuyển động miệng (lip-sync).
- [ ] Thêm biểu cảm cơ bản: vui, buồn, ngạc nhiên, suy nghĩ và trung tính.
- [ ] Điều khiển cử chỉ/animation từ các tín hiệu hội thoại của Ru.
- [ ] Đồng bộ audio text-to-speech với mouth movement và biểu cảm.
- [ ] Tạo chế độ an toàn: avatar không tự gửi lệnh hệ thống hoặc điều khiển máy tính ngoài phạm vi render/avatar.

## Thứ tự đề xuất

1. Hoàn tất base training và fine-tune hội thoại.
2. Thêm voice input/output.
3. Thêm avatar 2D hoặc 3D với lip-sync.
4. Thêm vision từ ảnh, sau đó mới đến webcam thời gian thực.
