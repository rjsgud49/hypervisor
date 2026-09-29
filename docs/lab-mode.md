# Lab Mode — 이 PC에서 바로, 끄면 원래대로

최종 목표(Type-1·H1 네이티브)와 **별도 트랙**이다.  
일상 Windows를 **메인**으로 유지한 채, **서브 Linux만 Docker로 기동/종료**한다.

| | Lab Mode (지금) | Target (연구 최종) |
|--|-----------------|-------------------|
| 메인 | 이 PC의 Windows (호스트) | 게스트 OS |
| 서브 | Linux 컨테이너(WSL2 VM 안) | 동등 게스트 |
| 끄기 | `lab/stop.ps1` → 서브·채널 제거, Windows 그대로 | 해당 없음 |
| H1 | **의도적 완화** (`docker`/`vmmem` 보일 수 있음) | 필수 |
| H3 | Windows↔컨테이너 프로세스 상호 미열거 **부분 충족** | 필수 |
| H2 | localhost 채널 (Lab) | HV 전용 채널 |

되돌림 원칙: **부트로더·디스크 파티션·드라이버를 건드리지 않는다.**  
compose down이면 Lab 흔적은 프로세스·컨테이너뿐.

---

## 빠른 사용

**완성형:** `lab/JoyLab.bat` 또는 `python lab/hvlab.py`  
→ 한 번 켜면 서브는 **계속 유지**. 창만 닫아도 OK.  
→ **재부팅하면 서브는 자동 OFF** (`restart: no`).  
→ 당장 끄기: `python hvlab.py --stop` / GUI「서브 OS 끄기」.

```powershell
cd lab
python hvlab.py
```

수동: [`../lab/README.md`](../lab/README.md)

---

## 왜 이 방식인가

1. **즉시성** — Hyper-V/QEMU 신규 설치·재부팅·GPU 패스스루 없이 Docker만으로 서브 OS 역할 검증
2. **가역성** — stop 한 번이면 일상 PC로 복귀
3. **연구 정렬** — 채널 프로토콜·H3 스모크·opcode 초안을 Lab에서 먼저 검증 후 Target으로 이식 ([phase1.md](experiments/phase1.md))

Target Type-1은 **별도 머신 또는 이중 부팅 연구 호스트**에서 진행하는 것이 “끄면 원래 Windows”와 충돌하지 않는다.
