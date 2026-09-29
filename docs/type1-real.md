# Type-1 실제 버전 — 설치·운영 가이드

대상 머신 예: Lenovo `21MECTO1WW` / AMD Ryzen 7 PRO 8840HS / NVMe 1TB (Windows 단일 파티션).

Lab ≠ 실제. 실제 = **관리 Linux + KVM + 게스트 OS 2개**.

---

## 0. 결정 (필수)

Type-1을 쓰려면 다음 중 하나:

| 옵션 | 장점 | 단점 |
|------|------|------|
| **A. 이중 부팅** (권장) | 같은 노트북, 재부팅으로 Windows↔연구 전환 | C: 축소·GRUB 필요, 백업 필수 |
| **B. 외장 NVMe에 Ubuntu** | 내장 Windows 덜 건드림 | 외장 디스크 필요 |
| C. 별도 데스크톱 | IOMMU/패스스루 여유 | 장비 추가 |

“Windows 켠 채로 진짜 Type-1”은 **불가**에 가깝다 (지금 호스트가 이미 Windows+Docker 하이퍼바이저).

재부팅 → Windows = 일상(Type-1 오프).  
재부팅 → Ubuntu 관리 호스트 = 실제 버전 온.

---

## 1. BIOS

부팅 시 BIOS/펌웨어:

- [ ] AMD SVM (가상화) Enable
- [ ] IOMMU / AMD-Vi Enable (명칭은 보드마다 다름)
- [ ] Secure Boot는 초기엔 Disable 권장 (서명 모듈·VFIO 단순화)
- [ ] 외장 부팅 시 Boot Order에서 USB/NVMe 우선

---

## 2. 디스크 (옵션 A)

1. Windows 백업
2. `디스크 관리` → C: **축소** → 미할당 **64GB 이상** (여유 있으면 128GB)
3. Ubuntu 24.04 ISO USB로 부팅
4. 미할당 영역에 Ubuntu 설치 (Windows 부트로더 덮어쓰기 주의 → “alongside” 또는 수동 파티션)
5. 설치 후 GRUB에서 `Ubuntu` / `Windows Boot Manager` 선택 확인

옵션 B: 외장 디스크에 Ubuntu만 설치, 내장 디스크는 그대로.

---

## 3. 관리 호스트 패키지

Ubuntu에 로그인 후 저장소를 마운트/클론하고:

```bash
cd /path/to/hypervisor/target/host
chmod +x *.sh
./check-hw.sh
sudo ./install-packages.sh
```

`check-hw.sh`가 기대하는 것:

- `kvm` 모듈
- `/dev/kvm`
- `dmesg`에 AMD-Vi/IOMMU
- (Phase 1) VFIO는 나중 — 일단 가상 디스크만

---

## 4. 게스트 디스크·기동

```bash
./create-disks.sh          # qcow2 2개 + 공유 ivshmem 파일
./run-guests.sh            # Main/Sub QEMU 기동 (시리얼 콘솔)
```

기본 사양 (RAM 여유에 맞게 `run-guests.sh`에서 조절):

| | Main | Sub |
|--|------|-----|
| vCPU | 4 | 2 |
| RAM | 4G | 2G |
| disk | `main.qcow2` | `sub.qcow2` |

최초에는 클라우드 이미지 또는 최소 ISO로 게스트 OS 설치 (스크립트가 cloud image URL을 안내).

---

## 5. Phase 1 검증

게스트 각각에서:

1. H3 마커 — [phase1.md](experiments/phase1.md) §3  
2. 채널 — `target/channel/hvchan_ivshmem.py` 로 ping (PCI ivshmem BAR mmap)  
3. [h1-checklist.md](h1-checklist.md) P01–P04, X01–X04

성공하면 **실제 버전 Phase 1 Done**.

화면 슬롯(Phase 2 입구): 관리 호스트에서 `python3 target/channel/fb_view.py --shm target/data/hvchan.shm --pull auto`  
→ `http://127.0.0.1:19721/` 에 Main VGA가 화면 레이아웃으로 표시된다. 상대 게스트 RAM은 읽지 않는다.

---

## 6. Lab과의 관계

| | Lab | Type-1 Target |
|--|-----|----------------|
| 서브 | Docker | QEMU/KVM 게스트 |
| 채널 | `127.0.0.1:19800` | ivshmem / HV ring |
| H1/H3 | 완화·부분 | 설계 목표 |
| 끄기 | `--stop` / 재부팅 | 관리 호스트 종료 또는 Windows로 재부팅 |

프로토콜 magic `HVC1`은 공유해 Lab에서 맞춘 메시지 포맷을 Target으로 이식한다.

---

## 7. 다음에 하지 않는 것

- 게스트가 다른 게스트 **메모리를 읽어 오버레이** (비범위)
- Windows 일상 세션 위에 Type-1을 “몰래” 얹기

---

## 관련

- [../target/README.md](../target/README.md)
- [experiments/phase1.md](experiments/phase1.md)
- [baseline.md](baseline.md)
- [isolation.md](isolation.md)
