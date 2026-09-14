# Nới lỏng backend (đợt 1) — design

Ngày: 2026-09-14. Mục tiêu: giảm friction (run chết oan, approve lặp, denied câm) mà không nới damage. Mọi đường nới đều có audit durable + tự rớt về hành vi cũ khi thiếu điều kiện.

## (a) Giữ prepared qua approval wait

- Vấn đề: policy Ask thì `AgentToolExecutionPipeline` discard hết prepared, model phải gọi lại y hệt sau approve.
- Đổi: khi Ask, stash `(proposal, preparedCall)` in-memory trong coordinator theo runId thay vì discard. `PreparedToolExecution` là pure-data (ExecutionId, ToolCallId, ModelOrder, AccessKind, PreparationToken ≤4096, PreparationPayloads byte-copy) — giữ được.
- Resume: đường mới trong pipeline — bỏ propose/evaluate, verify approvalId khớp toolCallId trong durable `ApprovalRequested`, commit `ToolExecutionRequested`, execute từ stash.
- An toàn: token approve 60s giữ nguyên; backend restart mất stash → fallback hành vi cũ.
- Test: Ask → approve → execute không re-propose; mất stash → model gọi lại như cũ.

## (b) Retry mutation có budget

- Vấn đề: retry chỉ cho read-only + transport; mutation fail là terminal; deny đầu tiên đã có thể giết run.
- Đổi 2 đường:
  1. Pipeline retry đúng 1 lần cho mutation fail do transport/unknown (mirror đường read-only).
  2. Deny đầu tiên mỗi (run, tool-identity): trả receipt Failed + repair note mang deny hint (kênh note sẵn có) để model sửa args gọi lại; deny giống hệt lần 2 mới terminal (dựa `ConsecutiveToolCallGuard` chống loop).
- Không retry mù cùng args (policy deterministic → deny lại y hệt, vô nghĩa).
- Test: deny lần 1 run sống + có note; deny lần 2 terminal; transport-mutation retry 1 lần.

## (c) Preset tin cậy opt-in + git sạch

- Vấn đề: approve lặp cho lệnh terminal trong workspace dù repo tin cậy.
- Đổi: thêm `Trusted=2` vào `AgentPermissionPreset`, lưu cùng session-settings store, set qua API (yêu cầu interactive như `full_access`). Evaluator: Trusted + git-clean + target trong workspace → Allow cho `terminal_external_mutation/unknown` và external_directory trong roots; vẫn Ask cho delete ngoài workspace, network, protected paths.
- Git-clean: check `git status --porcelain` lúc tạo run + mỗi Continue, cache theo run; repo bẩn giữa chừng → rớt về Standard.
- Không đụng `FullAccess`, không đụng run-creation map hiện tại ngoài việc chấp nhận thêm preset.
- Test: matrix trusted × git-clean × target kind.

## (d) Inspection gate hết câm + Continue mở

- Vấn đề: denied `inspection_required` chỉ nằm trong safeCode, user không biết vì sao và thoát ra sao.
- Đổi: khi gate deny, publish thêm activity `Status/Waiting` kèm Detail tiếng người (cần 1 read-only inspection, hoặc gửi Continue để override). `POST continue` clear gate cho run đó + commit durable audit event.
- GUI: verify Detail hiện cho Status row (metadata code đã hiện sau polish), thiếu thì thêm render.
- Test: denied có activity lý do; Continue clear gate + có audit; không Continue thì vẫn chặn.

## Ngoài scope

Full shadow-git checkpoints (làm sau nếu cần); OS-level sandbox; đụng `FullAccess`; retry mù deny trùng args.
