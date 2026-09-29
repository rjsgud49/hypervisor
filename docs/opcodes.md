# 제어 Opcode Allowlist

서브 → 메인 제어는 **로컬 효과만**. 상대(또는 임의) 프로세스 열거·디버그·메모리 접근은 프로토콜에 넣지 않는다.  
H3: [`isolation.md`](isolation.md)

상태: Phase 0 초안. Phase 3에서 스키마 고정.

---

## 허용 (allow)

| Opcode | 인자(개요) | 효과 | 비고 |
|--------|------------|------|------|
| `pointer.move` | x, y (절대 또는 델타) | 포인터 이동 | |
| `pointer.button` | button, down/up | 클릭 | |
| `pointer.scroll` | dx, dy | 스크롤 | |
| `key.event` | keycode/scancode, down/up | 키 | |
| `key.type` | utf8 text (길이 상한) | 단문 입력 | rate limit |
| `wheel` | (alias of scroll) | | 선택 |
| `display.roi_hint` | x,y,w,h | 캡처 ROI 힌트 (메인→서브가 주경로, 역방향은 선택) | PID 없음 |

창 관련은 OS별로 신중:

| Opcode | 인자 | 효과 | 제약 |
|--------|------|------|------|
| `window.focus_at` | x, y | 좌표 클릭으로 포커스 | 창 핸들/PID 전달 금지 |
| `window.bounds_query` | opaque_window_id? | bounds만 | **프로세스명·PID 반환 금지** |

---

## 금지 (deny — 구현·스키마 모두)

| Opcode / 행위 | 이유 |
|---------------|------|
| `process.list` / `process.find` | H3 파괴 |
| `process.read_memory` / `debug.attach` | H3·보안 |
| `fs.read_peer` / 공유 폴더 API | H1·H3 |
| `guest_info.hypervisor` 노출 API | H1 |
| 임의 `ShellExecute` / 무제한 `app.launch` | 초기엔 금지; 필요 시 별도 allow 항목으로만 |

---

## 응답

```text
status: ok | denied | timeout | error
# denied 시 detail에 상대 OS/PID 정보 넣지 말 것
```

---

## Rate limit (초안)

- pointer 이벤트: ≤ 200 Hz
- key.type: ≤ 32 chars / 요청, ≤ 5 요청/s
- 초과 시 `denied` + 감사 카운터

---

## 감사

로그 필드: `req_id`, `opcode`, `status`, `timestamp`  
금지: peer PID, peer process name, 화면 OCR 원문 전체(정책에 따름)
