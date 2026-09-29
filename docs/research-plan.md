# 연구 계획

## 목적

메인·서브가 **각자 고유 OS처럼 보이면서(H1)**, **HV 채널로 통신하고(H2)**, **프로세스끼리는 서로를 감지하지 못하는(H3)** 전제 아래  
`화면 → 연산 → 외부 HW → 메인 조작` 루프를 검증한다.

기준 문서: [isolation.md](isolation.md), [design.md](design.md)

---

## Phase 0 — 설계 고정 ✅

**산출물**

- `README.md`, `baseline.md`, `design.md`, `isolation.md`, `h1-checklist.md`, `opcodes.md`, `research-plan.md`, `threat-model.md`, `experiments/phase1.md`

**완료 기준**

- [x] H1/H2/H3 채택
- [x] 메인/서브 OS 후보 확정 → Linux/Linux ([baseline.md](baseline.md))
- [x] H1 탐지 체크리스트 초안 → [h1-checklist.md](h1-checklist.md)
- [x] 제어 허용 opcode 초안 → [opcodes.md](opcodes.md)
- [x] Phase 1 토폴로지 → [experiments/phase1.md](experiments/phase1.md)

---

## Phase 1 — 동등 게스트 + HV 채널 + 상호 미감지 ← **현재**

**상세 절차:** [experiments/phase1.md](experiments/phase1.md)

**목표:** 두 게스트 기동, HV 전용 ping/pong, H3 스모크 + H1 최소(P01–P04)

**작업**

1. 최소 관리 호스트 + KVM에서 Guest Main/Sub (Linux) 기동
2. HV 공유 링 + 커널 EP로 hello (TCP 피어 스캔 경로 금지)
3. 마커 프로세스로 상호 `ps` 0건 확인
4. [h1-checklist.md](h1-checklist.md) Phase 1 최소 항목 기록

**성공 기준**

- S1–S5 ([phase1.md](experiments/phase1.md) §5)
- H2: RTT p50/p95 문서화
- H3: X01–X04 Pass
- H1 최소: P01–P04 Pass

**실패 시**

- 채널만 Type-2로 프로토콜 검증 가능하나, **H1/H3 미달로 명시**하고 최종에 안 올림

---

## Phase 2 — 화면 공유 (지문 최소화)

**목표:** 메인 화면 → 서브, “가상 디스플레이/게스트 툴” 없이

**작업**

1. 메인 로컬 캡처 → 커널 EP → HV ring
2. 저해상도·저FPS로 E2E 지연 측정
3. 장치 관리자/`lspci`에 수상한 virt display 있는지 기록

**성공 기준**

- ≥10 fps급 동작 + 지연 분포
- H1 체크리스트에 신규 지문 항목 추가·제거 반복

---

## Phase 3 — 제어 (프로세스 열거 없이)

**목표:** 서브 → 메인 입력만

**작업**

1. opcode allowlist 고정 (PID 조회 없음)
2. 입력 주입 EP
3. 금지 opcode deny 테스트

**성공 기준**

- 클릭/키 재현
- 프로토콜/API로 상대·로컬 프로세스 목록이 나가지 않음

---

## Phase 4 — 외부 HW 패스스루

**목표:** 서브 전담 장치, 메인 장치 목록에 미등장

**성공 기준**

- 화면 조건 → HW 반응
- 메인에서 해당 PCI/USB 미열거 (또는 의도적 미할당)

---

## Phase 5 — H1 네이티브 외관 강화

**목표:** 상용·일반 탐지 표면 축소

**작업 (아키텍처·구성 수준)**

1. GPU/NIC/스토리지/입력 패스스루 확대
2. VirtIO·게스트 툴·공유 폴더 제거
3. H1 체크리스트(프로세스·서비스·장치·펌웨어 문자열 등) 회귀

**성공 기준**

- 체크리스트 항목별 pass/fail 표
- “완전 불가탐”이 아닌 **설계 목표 항목 통과**로 정의

---

## Phase 6 — 통합·평가

- 지연·CPU·대역 + H1/H3 회귀
- 한계(부채널 등)와 후속 과제 문서화

---

## 연구 질문 (RQ)

1. H1을 패스스루 중심으로 어디까지 실용적으로 줄일 수 있는가?
2. H2 채널을 커널 EP만으로 충분한 대역·지연으로 쓸 수 있는가?
3. H3을 깨는 가장 흔한 구성 실수(게스트 툴·Type-2)를 프로세스에서 배제할 수 있는가?
4. 화면·제어 메타데이터에서 PID를 빼도 연산·조작이 성립하는가?

---

## 리스크

| 리스크 | 영향 | 완화 |
|--------|------|------|
| GPU 패스스루 난이도 | H1·성능 | 단계적 장치 할당, 보조 GPU |
| “완전 안티VM” 기대 | 범위 폭발 | 체크리스트 한정, isolation.md 비범위 |
| Type-2로 빨리 가기 | H1/H3 문서와 모순 | 실험실 한정 라벨 강제 |
| 채널 디바이스 지문 | H1 | EP 최소화·정상 로컬 장치화 |

---

## 다음 액션

1. IOMMU 가능 머신에서 관리 호스트 준비 ([phase1.md](experiments/phase1.md) §6)
2. Guest Main/Sub 이미지 2개 생성 · H3 마커 테스트 (Step A)
3. HV 채널 Step B–D · `phase1-rtt.md` / `phase1-h1.md` 기록
4. (선택) Windows 메인 트랙은 Phase 1 Linux 통과 후 `baseline.md` 개정
