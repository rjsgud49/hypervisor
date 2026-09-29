# H1 탐지 표면 체크리스트

목표: 각 게스트 OS 안에서 **일반·상용 수준의 열거**로 “VM / 가상 서비스 / 하이퍼바이저 게스트”로 분류되지 않을 것.  
완전 불가탐이 아니라 **설계 목표 항목의 pass/fail**이다. 기준: [`isolation.md`](isolation.md) H1.

기록 방법: 각 행에 `P`(pass) / `F`(fail) / `N/A` + 메모. 메인·서브 **각각** 작성.

---

## 0. 메타

| 항목 | 값 |
|------|-----|
| 날짜 | |
| 빌드/커밋 | |
| 게스트 | Main / Sub |
| 하이퍼바이저 | |
| 패스스루 장치 요약 | |

---

## 1. 프로세스·서비스 (강함 — H1 핵심)

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| P01 | 프로세스 목록에 hypervisor/emulator 명 | `qemu`, `qemu-system`, `VirtualBox`, `VBox`, `vmware`, `xenguest`, `hv*` 관리툴 없음 | | | |
| P02 | 서비스/데몬 목록 | VMware Tools, VirtualBox Guest Additions, QEMU Guest Agent, Hyper-V 통합 서비스 없음 | | | |
| P03 | 예약된 “Guest Additions” 설치 흔적 | 패키지/설치 경로 없음 | | | |
| P04 | 상대 OS 관련 프로세스명 | 서브/메인 전용 데몬명이 상대에 보이지 않음 (H3와 교차) | | | |

---

## 2. 장치·버스 열거

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| D01 | PCI vendor/device | Red Hat VirtIO, VMware, VirtualBox, Xen netfront 등 **VM 전용 다수** 없을 것 (패스스루 실장치 위주) | | | |
| D02 | 디스플레이 | “가상 GPU만”이 아니라 패스스루 GPU 또는 합의된 예외 문서화 | | | |
| D03 | 디스크/NIC | 가능하면 실컨트롤러 패스스루; virtio-blk/net만으로 구성 시 Fail로 기록 후 Phase 5에서 축소 | | | |
| D04 | USB | 가상 USB 호스트 컨트롤러 지문 최소화; 외장 HW는 서브에만 | | | |
| D05 | ACPI/DMI 문자열 | `QEMU`, `VirtualBox`, `VMware`, `Bochs`, `Xen` 등 제품 문자열 최소화 | | | |

Linux 예: `lspci -nn`, `lsusb`, `dmidecode -s system-product-name`  
Windows 예: 장치 관리자, `msinfo32`, dxdiag

---

## 3. CPU·펌웨어 플래그

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| C01 | CPUID hypervisor bit | 게스트 앱이 흔히 읽는 하이퍼바이저 존재 비트가 **설계상 숨김/미설정**이거나, Fail이면 후속 과제 | | | |
| C02 | 하이퍼바이저 브랜드 리프 | “KVMKVMKVM” / “Microsoft Hv” / “VMwareVMware” 등 문자열이 유저 도구에 노출되지 않음 | | | |
| C03 | 타이밍 이상 | RDTSC/쿼리 성능이 “명백한 VM” 패턴이면 메모 (Phase 5+, 비보장) | | | |

> C01–C03은 난이도 높음. Phase 1에서는 **측정·기록**만 하고, Pass를 Phase 5 목표로 둔다.

---

## 4. 파일시스템·레지스트리·sysfs

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| F01 | 드라이버/모듈명 | `vboxguest`, `vmw_balloon`, `virtio_*` 대량 의존 없음 (패스스루 경로면 실드라이버) | | | |
| F02 | 장치 노드 | `/dev/vbox*`, `\\.\VBox*`, HGFS 마운트 없음 | | | |
| F03 | 공유 폴더/클립보드 서비스 | 비활성·미설치 | | | |
| F04 | Windows: `HKLM\...\Virtual Machine` 등 | 게스트 통합 키 없음 | | | |

---

## 5. 네트워크·이름

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| N01 | 호스트명/DNS | `*.localdomain` QEMU 기본, `virtualbox` 등 티 나는 이름 없음 | | | |
| N02 | MAC OUI | VBox/VMware 기본 OUI만 쓰지 않음 (패스스루 NIC면 OK) | | | |
| N03 | 브리지에 상대 게스트가 피어로 보임 | 일반 소켓으로 상대 OS가 “다른 호스트”로 열거되지 않음 (H2는 HV채널) | | | |

---

## 6. H3 교차 검사 (필수)

같은 시각, 양쪽 게스트에서:

| ID | 검사 | Pass 조건 | 결과 |
|----|------|-----------|------|
| X01 | `ps`/`tasklist` | 상대에서 돌린 **고유 마커 프로세스명**이 목록에 0건 | |
| X02 | 이름있는 파이프/소켓 목록 | 상대 PID와 상관 가능한 엔트리 없음 | |
| X03 | 디버거 attach to peer PID | 불가 | |
| X04 | 채널 API | `list_peer_processes` 등 없음 ([opcodes.md](opcodes.md)) | |

마커 예: 메인에서 `hv_main_marker_$$`, 서브에서 `hv_sub_marker_$$` 실행 후 교차 검색.

---

## 7. HV 채널 노출 표면

| ID | 검사 | Pass 조건 | M | S | 메모 |
|----|------|-----------|---|---|------|
| H01 | 유저랜드 포트 스캔 | vsock/CID·티 나는 TCP 포트가 “VM 채널”로 스캔되지 않음 | | | |
| H02 | 장치 이름 | `virtio-serial`, `vmbus` 등 대신 **합의된 로컬 EP** 또는 숨은 노드 | | | |
| H03 | 문서화되지 않은 PCI | 수상 장치 1개 이내 + 근거 메모 | | | |

---

## 8. 판정

| 단계 | 기준 |
|------|------|
| Phase 1 최소 | P01–P04, X01–X04 = 전부 P. D/C/F는 기록만 |
| Phase 5 목표 | §1–§2·§4–§7 핵심 ID = P. §3은 항목별 목표 명시 |
| 즉시 실패 | 게스트 툴 설치, 공유 폴더 ON, Type-2 호스트에서 UX=메인 |

실패 항목은 `docs/experiments/` 노트에 재현 명령과 함께 남긴다.

---

## 관련

- [isolation.md](isolation.md)
- [experiments/phase1.md](experiments/phase1.md)
- [opcodes.md](opcodes.md)
