# 연구 베이스라인 (Phase 0 고정안)

사용자 미지정 항목은 **연구 진행용 기본값**으로 잠근다. 실사용 OS가 다르면 이 문서만 개정하면 된다.

---

## 확정

| 항목 | 선택 | 근거 |
|------|------|------|
| 최종 배치 | Type-1 동등 게스트 2개 | H1·H3 ([isolation.md](isolation.md)) |
| Type-2 | 비정규·실험실 한정 | 호스트에 emulator 프로세스 노출 |
| 하이퍼바이저 (1순위) | **KVM** + 최소 Linux 관리 호스트 (UX 아님) | VFIO·문서·재현성 |
| 하이퍼바이저 (대안) | Xen (Dom0 최소) | 동등 DomU 모델 참고 |
| Phase 1 OS | **Main = Linux**, **Sub = Linux** | 패스스루·커널 EP·측정 용이 |
| 후속 트랙 | Main = Windows, Sub = Linux | 실사용 메인 대비; Phase 1 이후 |
| HV 채널 | 공유 메모리 링 + 양측 **커널 모듈 EP** | 유저랜드 VM소켓 지양 |
| 화면 (Phase 2) | 메인 PipeWire/DRM 캡처 → HV ring | 가상 모니터 추가 금지 |
| 제어 | [opcodes.md](opcodes.md) allowlist | PID API 없음 |
| 외부 HW (Phase 4) | USB serial / 단순 MCU → 서브 USB 패스스루 | 실기기 없으면 LED PoC |
| H1 평가 | [h1-checklist.md](h1-checklist.md) | Phase 1 최소 / Phase 5 목표 |
| **이 PC Lab** | Docker 테스트 전용 ([lab-mode.md](lab-mode.md)) | Type-1 아님 |
| **실제 버전** | [`type1-real.md`](type1-real.md) + [`../target/`](../target/) | 이중 부팅 Ubuntu + KVM Guest×2 |

---

## Lab Mode vs Target

- **Lab:** Windows 유지, Docker 서브 — 프로토콜만.
- **Target (실제):** 재부팅 → Ubuntu 관리 호스트 → 게스트 2개. 재부팅 → Windows면 Type-1 오프.

---

## 잠정 지연 예산 (실험 후 개정)

| 경로 | 잠정 목표 |
|------|-----------|
| HV ping RTT (Phase 1) | p95 < 1 ms (동일 머신) |
| Lab ping RTT (localhost) | 참고 측정 (`lab/main/channel_ping.py`) |
| 화면 E2E (Phase 2, 720p@10fps) | p95 < 100 ms (초기), 이후 축소 |
| 제어 RTT (Phase 3) | p95 < 20 ms |

---

## 패스스루 할당 (초안)

| 장치 | Main | Sub | 관리 호스트 |
|------|------|-----|-------------|
| GPU (주) | VFIO 패스스루 | — | 사용 안 함(또는 SOC 콘솔) |
| GPU (보조) / 없음 | — | CPU 또는 2nd GPU | — |
| 입력 (키보드/마우스) | 패스스루 | — | 시리얼 콘솔만 |
| 시스템 NIC | 패스스루 또는 없음 | 정책에 따름 | 관리용 NIC 1 |
| 외장 USB(HW) | — | 패스스루 | — |
| 스토리지 | 각각 전용 디스크/NVMe 파티션 | 동일 | 작은 root |

실제 보드/PC에 맞춰 `docs/experiments/phase1.md`에 머신별로 적는다.

---

## 비범위 (유지)

- 모든 타이밍 부채널 제거
- 안티치트 우회 “기법 목록” 제공 — 본 연구는 **구성·격리·패스스루**로 표면 축소
