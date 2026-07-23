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
- [x] Evaluate the local Qwen3.5-9B text-only base (physical peak 5,812 MiB; automatic 7/24 with English-language and mixed-language failures; do not fine-tune or adopt it; GUI remains v6).
- [x] Download and verify the local Llama-3.2-3B-Instruct text-only trial (two safetensors shards, 6,425,529,048 bytes; removed after comparison; GUI remains v6).
- [x] Evaluate the Llama-3.2-3B-Instruct trial with the bilingual conversation gate (automatic 5/24; physical peak 2,911 MiB; not adopted; GUI remains v6).
- [x] Download and integrity-check the official Gemma-4-E4B-Instruct checkpoint (15,992,595,884-byte safetensors; GUI remains v6).
- [x] Add an isolated current Transformers runtime and run the Gemma-4-E4B text-only routed baseline (automatic 20/24 after the `bầu bạn` gate correction; physical peak 5,901 MiB; four mixed-language failures; GUI remains v6).
- [x] Add a separate Tkinter Gemma-4-E4B base test GUI with the verified 4-bit CPU-embedding/GPU-decoder placement; keep the Llama v6 GUI default unchanged.
- [x] Harden the Gemma base test GUI against interactive repetition with EOS-aware decoding, repetition penalty, and 3-gram blocking; keep the measured evaluator path unchanged.
- [x] Add a separate Tkinter GUI for the diagnostic Gemma light adapter using the evaluator's shared-KV-safe PEFT/manual-greedy path; keep the v6 GUI default unchanged.
- [x] Run the bounded Gemma 4 Ru light QLoRA experiment (64 microsteps; validation loss 8.879111991988289; training physical peak 5,874 MiB; adapter evaluation 11/24 with 13 failures and 4,336 MiB physical peak); not adopted, GUI remains v6.
- [x] Retire the Gemma 4 line after comparison: remove local weights, adapters, runtime, generated evidence, and temporary GUIs; keep historical measurements and source notes, then focus on Llama.
- [x] Evaluate local Qwen2.5 7B-Instruct with the routed smoke (automatic 8/8, English 4/4, and hardware gate passed at 5,471.782 MiB; manual score 5/8 with Chinese-language contamination and mixed-language failures; do not continue it).
- [x] Evaluate local Vistral-7B-Chat GGUF Q4 with the routed smoke (hardware gate passed; automatic 7/8 passed; failed English echo and Vietnamese point-of-view; do not continue it).
- [x] Evaluate local Llama-3.1-8B-Instruct GGUF Q4 with the routed smoke (hardware gate passed; automatic 8/8 passed; manual score failed due to Vietnamese point-of-view error and failing to mix languages naturally; do not continue it).
- [x] Build and train the local Llama-3.1-8B-Instruct v6 QLoRA candidate from the 400-row Ru v6 corpus.
- [x] Implement the v6 friendly adult persona and GUI-controlled Llama generation settings.
- [x] Run the live 12-case `proactive_flirt_v6` adapter evaluation (automatic 11/12; one benign mixed-language refusal; peak 5,632.025 MiB).
- [ ] Manually review the saved v6 replies; do not call v6 adopted while the automatic and VRAM gates fail.
- [x] Curate, validate, and split the v7 conversation-focus corpus (180 rows; 135 train, 22 validation, 23 held-out; no graphic, violent, coercive, or possessive targets).
- [x] Train and live-evaluate the separate v7 Llama QLoRA adapter (validation loss 2.5561517639593645; automatic 9/16; manual 8/16; physical 6 GiB VRAM cap passed; not adopted; GUI remains v6).
- [x] Build, train, and review the isolated v7.1 repair adapter (300 rows; validation loss 2.5795224606990814; automatic 10/24; manual 14/24; original v7 slice 10/16; physical peak 5,874 MiB during training and 5,850 MiB during evaluation; not adopted; GUI remains v6).
- [x] Retire the local v7 and v7.1 adapter directories on 2026-07-23 by sending them to the Recycle Bin; preserve source code, datasets, metrics, and evaluators for history.
- [x] Build the quality-aware v8 corpus (336 rows; 262/37/37 split; 48 clean v6 replay rows and 288 v7.1 correction rows; no common-suite prompt leakage across splits).
- [x] Train the true v8 continuation from the v6 adapter (one epoch; 17 optimizer updates; validation loss 2.902062905801309; physical peak 5,865 MiB; allocator counter retained as diagnostic).
- [x] Evaluate v8 against v6 on the same 24-case suite (v6 11/24, v8 14/24; original v7 slice 12/16 automatic; v8 not adopted; GUI remains v6).
- [x] Build, train, and evaluate the bounded v6.1 natural-style continuation (120 rows; 3 epochs; validation loss 3.156519373257955; physical peak 5,841 MiB; common suite tied v6 at 11/24; not adopted; GUI remains v6).
- [x] Add the exact four-turn screenshot replay gate for v6.1 and record its failure (first turn generic-short on both v6 and v6.1).
- [x] Prepare the Ru v6.2 dataset-only candidate (2,400 rows; `1680/360/360` split; `vi=1200`, `mixed=720`, `en=480`; long-context and lower-NSFW quality gates pass; training not run; GUI remains v6).
- [ ] Repair v8's remaining mixed-language collapse, generic-short English replies, and missing follow-ups before another adapter run.
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
