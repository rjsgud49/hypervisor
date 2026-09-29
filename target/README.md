# Type-1 실제 버전 (Target)

Lab(`lab/`)은 Docker 프로토콜 테스트다.  
**실제 버전**은 이 디렉터리: 물리 머신에서 **KVM 관리 호스트 + Guest Main + Guest Sub**.

```
재부팅 → Ubuntu(관리 호스트) 선택 → 게스트 2개 기동  →  실제 HV 채널
재부팅 → Windows 선택             → 일상 PC (Type-1 꺼짐)
```

지금 Lenovo(21MECTO1WW, Ryzen 7 PRO 8840HS) Windows만으로는 Type-1이 **안 올라간다**.  
관리용 Linux를 **이중 부팅(또는 외장 NVMe)** 으로 깔아야 한다.

---

## 권한 (다시 고정)

| 계층 | 권한 |
|------|------|
| KVM 관리 호스트 | 최고 (VMM·IOMMU·채널 매핑) |
| Guest Main / Guest Sub | 동등 게스트 (서로 프로세스·메모리 직접 접근 불가) |

서브가 메인의 RAM을 읽는 “최고권한 OS” 모델은 **아님** ([isolation.md](../docs/isolation.md)).

---

## 이 노트북에서 가는 순서

1. **백업** 후 Windows에서 C: 여유(~165GB 중) **64GB+** 축소해 파티션 확보  
   또는 USB/외장 NVMe에 Ubuntu 설치 (일상 디스크 비침습)
2. Ubuntu Server 24.04 LTS 설치 (데스크톱 최소 또는 서버 + SSH)
3. BIOS: SVM(AMD-V), IOMMU 관련 옵션 ON
4. 관리 호스트에서:

```bash
cd target/host
./check-hw.sh
./install-packages.sh
./create-disks.sh
./run-guests.sh
```

5. 각 게스트 시리얼에서 H3 마커 + (이후) 채널 ping — [phase1.md](../docs/experiments/phase1.md)

상세 설치: [../docs/type1-real.md](../docs/type1-real.md)

---

## 디렉터리

| 경로 | 역할 |
|------|------|
| `host/` | 관리 호스트 스크립트 (체크·패키지·디스크·기동) |
| `channel/` | 게스트용 채널 (ivshmem ping, 화면 슬롯 수신·표시) |
| `../docs/type1-real.md` | 이중 부팅·BIOS·검증 전체 가이드 |

Lab과 프로토콜(`HVC1`)은 맞추되, 전송 매체가 **localhost TCP가 아니라 공유 메모리/HV** 이다.

---

## 화면 메모리

게스트 개인 RAM은 열지 않는다. 관리 호스트가 Main의 기존 VGA를 QMP로 읽어 `hvchan.shm` 슬롯(`0x1000`)에 넣고, 브라우저는 그 슬롯만 화면으로 그린다.

게스트가 떠 있는 관리 호스트에서:

```bash
python3 target/channel/fb_view.py --shm target/data/hvchan.shm --pull auto
```

브라우저: `http://127.0.0.1:19721/`

Main 게스트 프레임버퍼를 직접 슬롯에 넣을 때는 게스트 안에서 `python3 fb_publish.py --source fb`, 뷰어는 `--pull watch`.

---

## Windows 화면

신버전은 `win/hvshare`다. 구버전은 `win/HvShare.exe`, `win/HvView.exe`다. 중계 서버는 구버전 프로그램을 그대로 쓰되, 웹 페이지로 화면을 보여 주지 않는다.

| 파일 | 구분 | 역할 |
|------|------|------|
| `win/HvRelay.exe` | 서버 | 19723 포트. 화면을 받아 둔다. 창 제목은 **HV 중계 서버** |
| `win/hvshare/HvShare.exe` | 신버전 | `HvShare.sys`를 등록하고, 드라이버가 가진 프레임을 서버로 보낸다 |
| `win/hvshare/HvShareView.exe` | 신버전 | 서버에 올라온 화면을 창에 그린다. 웹 브라우저가 아니다 |
| `win/HvShare.exe` | 구버전 | 드라이버 없이 이 PC 화면을 서버로 보낸다 |
| `win/HvView.exe` | 구버전 | 브라우저로 보던 창. 지금은 신버전 `HvShareView`를 쓰라고 안내한다 |

보는 순서는 서버 → 신버전 `HvShare` → 신버전 `HvShareView`다.

`HvShare.sys`는 프로세스 목록에 나오지 않는다. 서비스 이름은 `HvShare`, 장치 경로는 `\\.\HvShare`다. 픽셀은 사용자 모드 `CopyFromScreen`으로 들어오고, 프레임을 보관하는 주체는 드라이버다. 서버로 나가는 것은 그 프레임을 사용자 모드에서 읽은 JPEG다. 다른 게스트의 RAM을 열지 않고, GPU 스캔아웃을 긁지 않는다. 드라이버는 테스트 서명이며, 테스트 서명이 켜져 있어야 신버전 `HvShare`가 서비스를 시작한다.
