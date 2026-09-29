# Dual-OS Hypervisor Research

기존 OS(메인)와 **동등한 수준**의 두 번째 OS(서브)를 하이퍼바이저 위에 올리고, 서브 OS가 메인 OS의 화면을 수신·연산한 뒤 외부 하드웨어 신호와 메인 OS 조작을 수행하는 구조를 연구한다.

> **권장 순서:** 구현보다 **설계 먼저**. 하드웨어 격리·화면 공유·제어 채널이 얽혀 있어 아키텍처를 고정하지 않으면 구현이 반복적으로 깨진다.  
> 베이스라인: [`docs/baseline.md`](docs/baseline.md) · 설계: [`docs/design.md`](docs/design.md) · Phase 1: [`docs/experiments/phase1.md`](docs/experiments/phase1.md)

---

## 목표

| 구분 | 내용 |
|------|------|
| 메인 OS | 사용자가 평소처럼 쓰는 1차 OS (화면·앱·입력의 본체) |
| 서브 OS | 메인과 **동등한 게스트**로 동작하는 2차 OS (관측·연산·외부 I/O·제어) |
| 하이퍼바이저 | 두 OS를 격리하면서, **프로세스에게는 보이지 않는** 통신 통로만 제공 |

서브 OS의 역할 파이프라인:

```
메인 화면 취득 → 서브에서 연산 → 외부 하드웨어 신호 → (필요 시) 메인 OS 조작
```

---

## 하드 제약 (필수)

| ID | 제약 | 의미 |
|----|------|------|
| H1 | **네이티브 OS로 보여야 함** | 타 프로그램이 “가상 서비스 / VM / 하이퍼바이저 게스트”로 인식하면 안 됨. 각 OS는 **고유한 일반 OS**처럼 보여야 함 |
| H2 | **OS 간 통신은 가능** | 화면·제어·상태 교환은 하이퍼바이저 중재 채널로 허용 |
| H3 | **프로세스 상호 감지 불가** | 한쪽 OS의 임의 권한 프로세스(관리자 포함)가 다른쪽 OS의 프로세스·서비스·세션을 열거·감지할 수 없음 |

함의:

- 공유 프로세스 네임스페이스·게스트 툴(VMware Tools류)·호스트에 뜨는 `qemu` 같은 **가상화 흔적 UI**는 목표와 충돌한다.
- 통신은 **커널/하이퍼바이저 전용 채널**에 두고, 일반 앱의 프로세스 목록·장치 목록·서비스 목록에 상대 OS가 나타나지 않게 한다.
- Type-2(호스트=메인 + 게스트=서브)는 H1·H3을 깨기 쉬워 **최종 모델이 아니다**. 상세: [`docs/design.md`](docs/design.md) §2·§3, [`docs/isolation.md`](docs/isolation.md).

---

## 왜 설계부터인가

1. **동등 수준 + 네이티브 외관(H1)** 때문에 Type-1 + 장치 패스스루가 기본이고, Type-2 PoC는 제약 위반 가능성이 크다.
2. 화면·제어 채널이 **앱에 가상화로 보이면 안 되므로** 장치·드라이버 지문을 설계 단계에서 막아야 한다.
3. H3(상호 미감지)과 H2(통신)를 동시에 만족하려면 “누가 채널 엔드포인트를 소유하는지”를 먼저 고정해야 한다.
4. 외부 하드웨어는 서브 전담 패스스루가 H1·격리와 맞는다.

→ 본 저장소는 **설계·연구 문서 우선**, 이후 PoC 단계로 진행한다.

---

## 상위 아키텍처 (요약)

```
┌─────────────────────────────────────────────────────────┐
│                    Hypervisor (Type-1 권장)             │
│  ┌─────────────────────┐    ┌─────────────────────────┐ │
│  │  Guest: Main OS     │◄──►│  Guest: Sub OS          │ │
│  │  - UI / Apps        │    │  - Vision / Compute     │ │
│  │  - Screen source    │    │  - Ext. HW driver       │ │
│  │  - Control agent    │    │  - Control planner      │ │
│  └──────────┬──────────┘    └────────────┬────────────┘ │
│             │  HV-only channel           │              │
│             │  (앱·프로세스에 비공개)     │              │
│             └────────────────────────────┘              │
│                         │                               │
│         PCI/USB passthrough (네이티브 장치처럼)          │
└─────────────────────────┼───────────────────────────────┘
                          ▼
                 External Hardware
```

**권장 기준선:** Type-1(또는 동등)에서 메인·서브 **둘 다 독립 게스트** + GPU/NIC 등 **패스스루로 베어메탈에 가깝게** + OS 간 통신은 **하이퍼바이저 전용 채널**(일반 프로세스 네임스페이스에 노출 금지).

Type-2(호스트에 QEMU 등)는 H1 위반 소지가 커서 **제약 검증용이 아닌 실험실 한정**으로만 다룬다.

---

## 핵심 연구 축

1. **격리·미감지 (H1/H3)** — 네이티브 외관, 프로세스 상호 불가시
2. **은닉 통신 (H2)** — 하이퍼바이저 전용 채널 vs 앱 가시 표면
3. **화면 파이프라인** — 캡처/공유가 “가상 디스플레이”로 지문 나지 않게
4. **제어 채널** — 상대 OS 프로세스 열거 없이 입력·조작만
5. **외부 하드웨어** — 서브 전담 패스스루
6. **보안·신뢰** — 채널 탈취·탐지 표면

---

## 문서 맵

| 문서 | 설명 |
|------|------|
| [docs/type1-real.md](docs/type1-real.md) | **실제 Type-1 설치·운영** |
| [target/README.md](target/README.md) | KVM 호스트 스크립트·채널·Windows 화면 |
| [docs/baseline.md](docs/baseline.md) | Phase 0 고정안 |
| [docs/design.md](docs/design.md) | 시스템 설계 |
| [docs/isolation.md](docs/isolation.md) | H1·H2·H3 |
| [docs/h1-checklist.md](docs/h1-checklist.md) | 검사표 |
| [docs/opcodes.md](docs/opcodes.md) | 제어 allowlist |
| [docs/research-plan.md](docs/research-plan.md) | 연구 단계 |
| [docs/experiments/phase1.md](docs/experiments/phase1.md) | Phase 1 검증 |
| [docs/lab-mode.md](docs/lab-mode.md) | Lab(Docker) 테스트 전용 |
| [lab/README.md](lab/README.md) | Lab 런처 |

---

## 단계별 진행 (개요)

| Phase | 내용 | 산출물 |
|-------|------|--------|
| 0 | 설계·H1/H2/H3·위협모델 | 본 docs |
| 1 | Type-1 동등 게스트 + HV 전용 채널 | 상호 미감지 + ping |
| 2 | 화면 공유 | 지연·탐지 표면 |
| 3 | 서브 → 메인 제어 | 입력 API만 |
| 4 | 외부 HW 패스스루 | 장치 전담 |
| 5 | 네이티브 외관 강화 | H1 체크리스트 |
| 6 | 통합·벤치 | 리포트 |

---

## 기술 후보 스택

- **하이퍼바이저:** KVM, Xen (관리 도메인은 최소·비UX)
- **장치:** VFIO PCI/GPU/USB 패스스루
- **게스트 통신:** HV 공유 페이지 / 커널 EP (`target/channel`)
- **화면·제어·HW:** design.md 기준 (PID/상대 메모리 읽기 없음)

---

## 저장소 상태

**실제 버전 = Type-1 Target.**  
이 Lenovo(Windows만)에서는 Type-1이 안 뜸 → Ubuntu **이중 부팅 또는 외장 NVMe** 후:

```bash
cd target/host
./check-hw.sh && sudo ./install-packages.sh
./create-disks.sh && ./run-guests.sh
```

가이드: [`docs/type1-real.md`](docs/type1-real.md)  
재부팅으로 Windows 선택 = Type-1 오프.  
`lab/` 는 프로토콜 테스트 전용.

---

## 라이선스 / 기여

연구용 개인·팀 저장소. 커밋 규칙·작성자 정책은 팀 규칙(`feat`/`chore` 등, 작성자 `rjsgud`)을 따른다.
