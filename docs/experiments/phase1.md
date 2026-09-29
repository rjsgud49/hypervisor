# Phase 1 — 동등 게스트 + HV 채널 + H3 스모크

목표: Type-1류에서 Main/Sub Linux 게스트 2개를 띄우고, **하이퍼바이저 중재 채널**로 ping/pong 하며, **상호 프로세스 미감지(H3)** 와 **프로세스·서비스 상 가상화 툴 부재(H1 최소)** 를 확인한다.

> **실제 구현 진입점:** [`../type1-real.md`](../type1-real.md) · [`../../target/`](../../target/)  
> Lab Docker는 이 Phase의 대체가 아니다.

베이스라인: [`../baseline.md`](../baseline.md)  
체크리스트: [`../h1-checklist.md`](../h1-checklist.md)
---

## 1. 토폴로지

```
┌──────────────────────────────────────────────────────────────┐
│ Physical machine                                             │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Management host (minimal Linux, NO daily UX apps)      │  │
│  │  - KVM/QEMU or Cloud Hypervisor as VMM                 │  │
│  │  - IOMMU/VFIO                                          │  │
│  │  - channel broker (shared pages between two VMs)       │  │
│  └────────────────────────────────────────────────────────┘  │
│       │ VFIO / virt (최소화)        │                        │
│       ▼                             ▼                        │
│  ┌─────────────┐  HV ring/doorbell  ┌─────────────┐          │
│  │ Guest Main  │◄──────────────────►│ Guest Sub   │          │
│  │ Linux       │   (kernel EP)      │ Linux       │          │
│  │ marker proc │                    │ marker proc │          │
│  └─────────────┘                    └─────────────┘          │
└──────────────────────────────────────────────────────────────┘
```

### 역할

| 노드 | 역할 | UX |
|------|------|-----|
| Management host | VMM·IOMMU·채널 매핑만 | SSH/시리얼만, 데스크톱 앱 금지 |
| Guest Main | 이후 화면·입력의 본체 | Phase 1에선 CLI+마커면 충분 |
| Guest Sub | 이후 연산·HW | 동일 |

### H1/H3과의 관계

- 관리 호스트에 `qemu`가 보여도 **사용자·타 프로그램이 보는 환경은 게스트**이므로, H1 평가는 **게스트 안**에서 수행한다.
- 게스트끼리 프로세스 테이블이 분리되어 있어야 X01 pass.

---

## 2. 채널 설계 (Phase 1 최소)

### 2.1 요구

- 양방향 메시지 ≤ 256 B
- 커널 또는 전용 캐릭터 디바이스 EP (`/dev/hvchan0` 가칭) — **일반 TCP로 상대 호스트 스캔 불가**
- 프로토콜에 PID/프로세스 목록 필드 없음

### 2.2 메시지

```text
magic = "HVC1"
type  = PING | PONG | TIME_REQ | TIME_RSP
seq   = u64
t_send_ns = u64   # PING/TIME
```

RTT = PONG 수신 시각 − PING 송신 시각 (게스트 TSC/시계 보정은 메모).

### 2.3 구현 단계 (코드는 이후 커밋)

| Step | 내용 | 비고 |
|------|------|------|
| A | 두 게스트 + 시리얼 콘솔 기동 | 채널 없음, H3만 |
| B | ivshmem 또는 VMM 매핑 공유 페이지 + doorbell | 실험용; 장치 지문은 H01에 기록 |
| C | 양측 커널 모듈 또는 최소 `mmap` 유틸 | 유틸이 “vmtool” 이름 금지 |
| D | ping 1000회, p50/p95 기록 | `results/phase1-rtt.md` |

> Step B의 ivshmem PCI는 H1 표면이 될 수 있다. Phase 1에서는 **동작 우선·지문 기록**, 은닉은 Phase 5.

---

## 3. H3 스모크 절차

1. Main: `sleep 3600 &` 를 고유명으로 — 예: `cp -a $(which sleep) /tmp/hv_main_marker && /tmp/hv_main_marker 3600 &`
2. Sub: `/tmp/hv_sub_marker 3600 &` 동일
3. Main에서 `ps aux | grep hv_sub_marker` → **0건**
4. Sub에서 `ps aux | grep hv_main_marker` → **0건**
5. 결과 표: [h1-checklist.md](../h1-checklist.md) X01–X03

---

## 4. H1 최소 절차 (게스트 내부)

- [h1-checklist.md](../h1-checklist.md) §1 P01–P04
- 게스트 에이전트/툴 미설치 확인
- D01/D05는 스냅샷만 (Fail 허용, 기록 필수)

---

## 5. 성공 기준 (Phase 1 Done)

| ID | 기준 |
|----|------|
| S1 | Main·Sub 동시 부팅 안정 (각 30분+) |
| S2 | HV 채널 ping p95 측정값 문서화 |
| S3 | X01–X04 = Pass |
| S4 | P01–P04 = Pass |
| S5 | 토폴로지·명령·실패 항목이 본 디렉터리에 남음 |

---

## 6. 머신 준비 체크 (호스트)

관리 호스트에서 확인:

- [ ] CPU VT-x/AMD-V + IOMMU (`intel_iommu=on` / `amd_iommu=on`)
- [ ] `kvm` 모듈 로드
- [ ] 게스트용 디스크 이미지 2개 (또는 공유 템플릿 클론)
- [ ] 입력 장치 패스스루 전 — Phase 1은 시리얼/VNC는 **관리용만**, 일상 앱은 게스트에서 평가
- [ ] 외장 HW는 Phase 4까지 연결 안 해도 됨

### 권장 게스트 사양 (최소)

| | Main | Sub |
|--|------|-----|
| vCPU | 2+ | 2+ |
| RAM | 2 GB+ | 2 GB+ |
| disk | 20 GB+ | 20 GB+ |
| console | serial | serial |

---

## 7. 산출물 템플릿

이 디렉터리에 추가할 파일:

| 파일 | 내용 |
|------|------|
| `phase1-host.md` | 실제 호스트 CPU/보드, IOMMU 그룹 |
| `phase1-rtt.md` | ping 통계 |
| `phase1-h1.md` | 체크리스트 복사본 + P/F |

(실측 전이라 아직 생성하지 않음.)

---

## 8. 다음 (Phase 2 예고)

- Main 캡처 → 동일 HV ring으로 프레임 헤더+payload
- [opcodes.md](../opcodes.md)는 Phase 3
- 패스스루 GPU는 Phase 5와 병행 가능

---

## 관련

- [../type1-real.md](../type1-real.md) — 실제 설치
- [../../target/README.md](../../target/README.md)
- [../baseline.md](../baseline.md)
- [../research-plan.md](../research-plan.md)
- [../design.md](../design.md)
