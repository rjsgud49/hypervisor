# 시스템 설계 — Dual-OS Hypervisor

## 1. 문제 정의

하나의 물리 머신에서:

- **메인 OS**: 일상 UI·앱·사용자 입력의 본체 — **고유한 일반 OS처럼 보여야 함**
- **서브 OS**: 메인과 **동등·독립**인 2차 OS — 역시 가상 서비스가 아닌 **고유 OS**로 보여야 함
- 서브는 (1) 메인 화면 수신 (2) 연산 (3) 외부 HW 신호 (4) 필요 시 메인 조작

하드 제약 **H1·H2·H3**은 [`isolation.md`](isolation.md)가 기준이다.

| ID | 요약 |
|----|------|
| H1 | 타 프로그램이 VM/가상 서비스로 인식하면 안 됨 |
| H2 | OS끼리 통신 가능 (하이퍼바이저 중재) |
| H3 | 각 OS의 프로세스는 상대 OS 프로세스를 감지·열거 불가 (권한과 무관) |

---

## 2. 설계 원칙

| 원칙 | 설명 |
|------|------|
| Native appearance (H1) | 각 게스트는 패스스루·지문 최소화로 “그냥 OS” |
| Invisible peer (H3) | 프로세스 네임스페이스·도구·API로 상대가 안 보임 |
| HV-only channel (H2) | 통신은 하이퍼바이저 전용. 상대 PID/프로세스 목록은 프로토콜에 없음 |
| Isolation first | 메모리·CPU·장치는 분리. 공유는 명시적 HV 채널만 |
| Main is primary UX | 사용자는 메인을 본다 |
| Sub owns side effects | 외부 HW·무거운 연산은 서브 |
| No cross-debug | 한쪽이 다른쪽 메모리를 디버그·덤프하는 경로를 게스트에 제공하지 않음 |
| Measurable | 지연·탐지 표면 체크리스트를 수치/항목으로 남김 |

---

## 3. 배치 모델 (선택지)

### 3.1 목표 모델 — Type-1 동등 게스트 + 패스스루 (**유일한 H1/H3 적합 최종형**)

```
Hardware
  └─ Hypervisor (+ 최소 관리 도메인, UX 아님)
        ├─ Guest A: Main OS   (GPU/입력 등 패스스루, HV채널 커널 EP)
        └─ Guest B: Sub OS    (연산·외장 HW 패스스루, HV채널 커널 EP)
```

- **장점:** 동등 수준, 프로세스 테이블 분리(H3 기본 충족), 호스트에 qemu 프로세스 없음(H1)
- **단점:** GPU/입력 패스스루·이중 이미지·채널 은닉이 어렵고 비용이 큼

### 3.2 실험실 한정 — Type-2 (호스트=메인)

```
Hardware
  └─ Main OS (host)  ← 여기서 이미 게스트/qemu가 프로세스에 보임
        └─ Guest: Sub OS
```

- **H1·H3 위반:** 호스트 프로세스가 게스트를 감지·제어 가능, 가상화 흔적 노출
- **용도:** 채널 프로토콜·화면 코덱만 단독 검증할 때의 **비정규 샌드박스**. 최종 아키텍처로 채택하지 않음

### 3.3 비권장

- 듀얼부트: 동시 실행·H2 불가
- 컨테이너/동일 커널: “고유 OS”·H3 불충분
- 게스트 툴·공유 폴더·클립보드 공유: H1·H3 파괴

---

## 4. 논리 컴포넌트

### 4.1 Hypervisor Plane

- 두 게스트 스케줄·메모리 격리·IOMMU
- **앱에 안 보이는** 전용 채널 (공유 페이지 / 하이퍼콜 링)
- VFIO 패스스루
- 관리 콘솔은 게스트 유저랜드에 노출하지 않음

### 4.2 Main Guest

| 모듈 | 역할 | H3 주의 |
|------|------|---------|
| Screen Source | 로컬 캡처 → 커널 EP로 enqueue | 상대 PID 모름 |
| Control Endpoint | 입력 주입만 수행 | `list_peer_procs` 없음 |
| (선택) 최소 에이전트 | 커널 EP 유저 헬퍼 | 서비스명이 “VM tools”면 안 됨 |

### 4.3 Sub Guest

| 모듈 | 역할 |
|------|------|
| Frame Ingress | HV 채널에서 프레임 수신 |
| Compute Pipeline | 연산 |
| Actuator | 외장 HW |
| Control Planner | 입력 명령만 송신 (메인 프로세스 조회 없음) |

### 4.4 External Hardware

- 서브 **전담 패스스루** (메인의 장치 목록에 안 뜨게)

---

## 5. 데이터 경로

### 5.1 화면: Main → Sub

**목표 경로 — HV 공유 링 (유저에 “네트워크 피어”로 안 보임)**

```
Main capture → kernel EP → HV ring → Sub kernel EP → compute
```

실험용 공개 소켓 스트림은 지문·포트 스캔 표면이 커 H1과 충돌하기 쉬움 → 커널 EP 뒤로 숨기거나 후반에만.

메타데이터: 해상도, stride, timestamp, frame id, ROI.  
**포함 금지:** 캡처 대상 프로세스 PID, 창 owner PID (필요 시 익명 ROI만).

### 5.2 제어: Sub → Main

```
Sub planner → HV channel → Main control EP → 로컬 입력 API
```

명령 예: pointer/key/window focus 등 **로컬 효과만**.  
**금지 opcode:** 상대 프로세스 열거, 원격 메모리 읽기, 디버그 attach.

### 5.3 외부 신호: Sub → Hardware

서브 드라이버 → 패스스루 장치. 메인 프로세스와 무관.

### 5.4 상태 텔레메트리

창 bounds·포커스 등 **익명화된 UI 상태**만. 프로세스 목록·경로·토큰 금지.

---

## 6. 인터페이스 스케치 (초안)

### 6.1 FrameHeader

```text
magic: u32
version: u16
width, height: u16
format: u16
stride: u32
timestamp_ns: u64
frame_id: u64
flags: u32
roi: {x,y,w,h} optional
# NO: pid, process_name, hwnd owner identity (optional opaque window_id only)
```

### 6.2 ControlRequest

```text
req_id: u64
opcode: enum   # input/window only
args: opaque
deadline_ms: u32
```

### 6.3 ControlResponse

```text
req_id: u64
status: ok | denied | timeout | error
detail: string/code
```

---

## 7. OS 조합 후보

| 메인 | 서브 | 비고 |
|------|------|------|
| Windows | Linux | 패스스루·채널 은닉 난이도 높음 |
| Linux | Linux | 연구·패스스루 실험 용이 |
| Windows | Windows | 무거움 |

---

## 8. 자원·스케줄

- 게스트별 vCPU·메모리 분리, IOMMU
- GPU: 메인 패스스루 우선 (가상 GPU 지문 회피). 서브는 보조 GPU 또는 CPU
- 외장 HW 인터럽트 → 서브만

---

## 9. 실패·안전

| 상황 | 동작 |
|------|------|
| 서브 크래시 | 메인 UX 유지, 채널 idle, HW fail-safe |
| 메인 EP 다운 | 서브 safe-stop |
| 채널 단절 | 재연결은 커널 경로만 |
| 금지 opcode | deny + audit (상대 정보 미포함) |

---

## 10. 설계에서 먼저 고정할 결정

- [x] H1/H2/H3 → [`isolation.md`](isolation.md)
- [x] 메인/서브 OS (연구) → Linux/Linux [`baseline.md`](baseline.md)
- [x] 패스스루 할당 초안 → [`baseline.md`](baseline.md)
- [x] HV 채널 → 공유 링 + 커널 EP [`experiments/phase1.md`](experiments/phase1.md)
- [x] 제어 opcode → [`opcodes.md`](opcodes.md)
- [x] H1 검사표 → [`h1-checklist.md`](h1-checklist.md)
- [ ] 지연 예산 실측값으로 확정 (Phase 1–3)
- [ ] Windows 메인 트랙 일정

---

## 11. 관련 문서

- [baseline.md](baseline.md)
- [isolation.md](isolation.md)
- [h1-checklist.md](h1-checklist.md)
- [opcodes.md](opcodes.md)
- [research-plan.md](research-plan.md)
- [threat-model.md](threat-model.md)
- [experiments/phase1.md](experiments/phase1.md)
- [../README.md](../README.md)
