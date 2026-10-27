# Desktop 1.0.2 수정 및 실제 설치 검증

2026-10-27 · 검증 코드 `0534ab9` · [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/40447002247)

사용자가 1.0.1에서도 동작 실패를 보고하여 신규 설치뿐 아니라 기존 설치 업데이트, 저장된 정책 편집, 실제 삭제·복구, 배포 실행기와 실패 복원으로 검증을 확대했다. 정확한 운영 PC 오류는 제공되지 않아 해당 PC의 해결을 직접 확인한 것은 아니다.

## 수정한 원인

- Dashboard의 고정 버전 문자열을 실제 assembly 버전으로 교체했다. Desktop/Service 버전이 다르면 연결된 상태에서도 업데이트 안내가 나타난다.
- 모든 IPC가 관리자 토큰을 요구하는데 Desktop은 asInvoker로 실행하던 불일치를 수정했다. 일반 실행에서 UAC를 요청하며 Visual Studio 디버깅은 관리자 실행을 사용한다.
- 주기적 갱신에서 PolicyEditor를 계속 새로 만들어 입력·오류 상태를 초기화하던 코드를 ID/version 기반으로 변경했다. 여러 정책의 미저장 초안을 유지하고 삭제 명령은 선택한 초안만 제거한다.
- 작업 완료·캐시 변화 및 History 화면에서 이력을 갱신한다.
- 실행 중 설치본을 덮어쓰지 않고 정확한 경로의 Desktop 종료 및 서비스 종료를 확인한 뒤 staged 파일로 교체한다. 실패 시 이전 바이너리를 복원하고 기존 실행 서비스를 다시 시작한다. 설정·정책·이력 데이터는 제거하지 않는다.
- Setup.cmd가 설치 프로세스 자체의 완료를 기다리도록 했다. Uninstall도 Desktop 프로세스 종료를 확인한 뒤 파일을 제거한다.
- 설치 transcript, Desktop 시작/오류 로그, Diagnostics logs 버튼을 제공한다. 요청 본문과 FTP 비밀번호는 로그에 쓰지 않는다. 선택적 센서/자동 시작 작업 등록 실패가 핵심 서비스 설치를 막지 않게 했다.

## 검증 결과

- Windows .NET Framework4.8/x64 빌드 및 WPF design-time 참조 평가 성공, **246/246 시험 통과**.
- 서비스 없는 실제 Desktop: 로컬 볼륨/CPU 실측, 설치 안내, X 종료.
- 실제 설치 버튼 → 인증 IPC → 물리 드라이브/CPU/disk I/O → 볼륨 선택 → 정책 오류 수정 → 저장/Preview.
- 저장된 정책에 잘못된 숫자를 입력한 뒤 3초 polling이 지나도 입력값과 빨간 표시가 유지되며, 수정하면 정상 저장.
- 실제 fixture 삭제, UI History의 DeletedConfirmed 갱신, RAM 복구 후 원본 바이트 일치, Settings 저장, Hide/Show/Exit.
- 실제 1.0.1 소스를 빌드·설치하고 그 Desktop이 실행 중인 상태에서 업데이트. 새 서비스 버전 및 기존 정책/설정 보존 확인.
- 동일 버전의 실행 중 설치본 재설치, 공백/한글 경로에서 배포 ZIP의 Setup.cmd 실행과 설치된 Desktop 시작 확인.
- 서비스 의존성 DLL을 제거한 시험 패키지로 시작 실패 유도. 이전 DLL·바이너리 복원과 서비스 Running 상태 확인.
- 실제/예시 WPF 화면45장, 바인딩 오류0건. 실제 서비스 설치·IPC·렌더·제거 모두 성공.
- 보조 검증: macOS의 portable source 시험111개 통과, Framework4.8/C#7.3 의미 컴파일 통과.

`desktop-offline.log`, `desktop-workflow.log`, `desktop-upgrade.log` 및 Windows 작업 로그에 각 PASS를 남겼다. 손상된 패키지의 실패는 의도한 rollback 시험이며 마지막 서비스 연동 시험은 복원 후 새로 설치한 정상 패키지로 통과했다.

## 적용

전체 ZIP을 압축 해제하고 **Setup.cmd**를 실행하면 기존 설치까지 함께 교체한다. Desktop/Service 버전은 왼쪽 아래에서 확인한다. 서비스 재시작으로 RAM 복구본이 사라지므로 필요한 파일을 먼저 복구한다. 데이터베이스의 정책·설정·이력은 유지된다. 설치/앱 진단은 `%LocalAppData%\ExplorerManager\Logs`에 저장된다.

운영 PC의 실제 하드웨어 온도 센서와 상위 FTP 서버는 해당 장비/서버에서 검증해야 한다. 이 수정은 수신 서버나 프로토콜을 변경하지 않는다.

---

## 이전 1.0.1 검증 기록

### Desktop 1.0.1 수정 결과

2026-10-27 · 검증한 코드 `c581f87` · [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/40428806793)

이전 완료 보고는 설치 후 연결·렌더 시험을 사용자 첫 실행과 실제 입력 흐름까지 검증한 것으로 확대했다. 서비스 미설치, 창 닫기, 필드별 오류와 실제 가상 서비스 계정의 사용자 폴더 권한을 검증 범위에 추가했다.

## 수정

- X/Exit는 Desktop 프로세스를 종료한다. Hide to tray만 숨긴다. 서비스는 독립 실행한다. 관리자 재실행은 기존 UI를 닫는다.
- 서비스가 없어도 실제 로컬 볼륨·CPU·RAM을 읽기 전용으로 조회한다. 미설치/중지/권한 상태를 안내하고 앱에서 설치·시작한다. F5 빌드는 서비스/센서와 Setup 파일을 준비한다.
- 정책 검증은 항목별로 표시하고 원인 목록을 제공한다. 정상 필드가 다른 필드의 오류로 빨갛게 되지 않는다. 입력 수정과 볼륨 선택을 보존하며 저장 version/갱신 경합도 처리한다.
- 실제 서비스 계정의 Access denied를 재현하고 Grant access와 권한 스크립트를 수정했다. 관리 폴더에 M, 상위 경로에는 비상속 RA/X만 부여하고 볼륨 루트 ACL은 변경하지 않는다. MAXIMUM_ALLOWED 핸들의 SetSecurityInfo로 관계없는 하위 트리의 ACL 전파를 방지한다.
- 부가 목록 조회 실패가 정상 Dashboard·실측을 지우지 않도록 격리한다. PDH 실패는 상태로 표시하고 CPU는 native GetSystemTimes fallback을 제공한다. 로컬 이동식/조회 실패 볼륨도 표시한다.

## 실제 검증

Framework4.8/x64 MSBuild, WPF 디자인 참조 평가, **243/243개 시험**, 기본/최소 크기의 실제/예시 화면45장과 바인딩 오류0건을 확인했다. 별도 실제 WPF 흐름은 예시 데이터 없이 다음을 검증했다.

1. 서비스 없는 Desktop 시작: 실제 로컬 볼륨·CPU 조회, 설치 안내와 X 종료.
2. F5 출력의 설치 버튼: 실제 SCM 서비스/센서 설치, 인증 IPC 연결, CPU/물리 디스크 처리량 조회.
3. Target volume 항목의 실제 표시와 선택, 잘못된 숫자/보관 기간 입력과 수정 후 오류 해제.
4. 가상 서비스 계정에 사용자 폴더 권한 부여 후 실제 정책 저장, 서버 DB에서 원본/볼륨/비활성 상태 확인.
5. 실제 오래된 fixture 파일을 Preview로 검사하고 WouldDelete 이력과 원본 보존 확인, X 종료.

`desktop-offline.log`와 `desktop-workflow.log`가 각각 PASS이며 서비스 설치/제거·기존 NTFS 삭제/복구·FTP 회귀도 통과했다. macOS 공유 코드108건과 PowerShell 내장 C#의 Framework4.8/C#5 의미 컴파일은 보조 검증이다.

## 적용

기존 설치는 새 전체 패키지의 Install.ps1로 서비스를 업데이트한다. 새 Desktop.exe만 실행하면 이미 실행 중인 구버전 서비스를 자동 교체하지 않는다. 최초 실행은 서비스 설치/시작 버튼을 사용하고, 사용자 폴더 접근 거부는 Policies의 Grant access에서 권한을 부여한 뒤 다시 저장한다. 운영 PC에는 직접 접속하지 않았으며 온도 센서 지원 여부는 장비·드라이버에 따른다.
