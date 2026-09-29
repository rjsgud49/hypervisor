# Lab — 가역 실험 환경

## 라이프사이클

| 동작 | 결과 |
|------|------|
| `JoyLab.bat` / `python hvlab.py` | 서브 OS **켜짐** + 채널 연결 |
| 창 닫기 / CLI Enter | 채널만 끊김, **서브는 계속 실행** |
| `python hvlab.py --stop` 또는 `.\stop.ps1` 또는 GUI「서브 OS 끄기」 | 서브 **즉시 종료** |
| **노트북 재부팅** | `restart: no` → 서브 **자동으로 안 켜짐** (꺼진 상태) |

---

## 실행

1. Docker Desktop 실행
2. `JoyLab.bat` 더블클릭

```powershell
cd lab
python hvlab.py
python hvlab.py --status
python hvlab.py --stop
```

---

## 수동 스크립트

| 스크립트 | 동작 |
|----------|------|
| `.\start.ps1` | 서브만 기동 |
| `.\stop.ps1` | 서브 종료 |
| `.\status.ps1` | 상태 |
| `.\h3-smoke.ps1` | H3 마커 검사 |

---

## 복구

- 재부팅만으로도 서브는 다시 안 올라옵니다 (`docker-compose.yml` → `restart: "no"`).
- 부트로더·파티션은 변경하지 않습니다.

[`docs/lab-mode.md`](../docs/lab-mode.md)
