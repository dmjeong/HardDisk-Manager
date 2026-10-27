# Desktop 기본 동작 복구 설계

2026-10-27 · [계획](../../01-plan/features/desktop-reliability.plan.md)

## 연결과 실행

Client에 읽기 전용 Windows desktop diagnostics를 둔다. SCM 상태/관리자 여부, 로컬 볼륨, GetSystemTimes CPU, 가용 RAM을 독립적으로 조회한다. 서비스 Endpoint PID 검증·관리자 IPC 권한은 완화하지 않는다. Presentation은 선택적 interface로 진단 스냅샷을 받고 로컬 조회와 서비스 상태를 구분한다.

Setup service는 관리자 UAC 재실행 후 기존 서비스는 시작만 하고, 미설치이면 완전한 패키지의 Install.ps1을 실행한다. F5 빌드에는 ReferenceOutputAssembly=false 서비스/센서 프로젝트와 별도 Setup staging을 준비한다. 이미 실행 중인 서비스를 자동 재설치하거나 RAM 캐시를 지우지 않는다.

선택적 Desktop host interface로 Exit/Hide/Setup을 실행하고 플랫폼 코드를 VM에서 분리한다. X는 정상 UI 종료, Hide만 창을 숨긴다. 서비스는 독립 실행을 유지한다.

## 오류와 실측

정책 INotifyDataErrorInfo를 필드별 목록으로 바꾸고 ErrorsChanged를 관련 필드마다 알린다. 요약 목록으로 원인을 보여 주며 일반 입력 형식 오류도 저장 시 안내한다. 비어 있는 ComboBox 전이는 유효한 볼륨 ID를 지우지 않는다.

Dashboard 수신 성공 후 Recovery/History/Transfers 실패는 연결 상태와 실측을 지우지 않는다. 연결 실패 시 로컬 실측과 원인을 보여 주며 삭제 관련 명령은 서비스 연결 전 거부한다.

Monitoring은 GetSystemTimes CPU fallback과 PDH 실패 상태를 제공한다. 일부 카운터 미지원은 다른 실측 수집을 중단시키지 않는다. 볼륨 탐색은 로컬 고정/이동식 볼륨을 표시하고 미지원/접근 실패를 숨기지 않는다. 실제 삭제 시 NTFS와 native 식별 검증은 유지한다.

## 검증

필드별 오류와 수정 후 해제, 신규 정책 편집 보존, 부가 조회 실패 격리, 로컬 진단 상태를 단위 시험한다. Windows에서는 실제 미설치/설치된 서비스, CPU/볼륨 실측, WPF ComboBox 항목·텍스트 입력·오류 해제·정책 저장·X 종료를 확인한다. 예시 데이터 렌더링은 동작 검증으로 세지 않는다.
