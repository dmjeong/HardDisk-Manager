# Desktop 신뢰성 문제 분석

2026-10-27 · 이전 완료 판단의 검증 범위가 사용자 실행 경로를 충분히 포함하지 못했다. 설치 후 IPC·렌더링 성공을 서비스 미설치/F5/실제 입력 동작의 성공으로 확대해 해석하지 않는다.

| 사용자 증상 | 확인한 원인 | 수정과 검증 범위 |
|---|---|---|
| 종료 안 됨 | Closing 이벤트가 항상 취소되고 Hide 호출. 별도 트레이 메뉴를 찾아야 종료 | X/Exit는 UI 종료, Hide to tray 분리. 미설치 상태 실제 Desktop 프로세스의 X 종료 확인 |
| Target volume/CPU 없음 | 서비스가 없으면 모든 데이터 조회가 실패. Desktop 단독 시작 시 준비 절차 부재 | 로컬 실제 볼륨/CPU/RAM 읽기 전용 진단, SCM 상태 안내, 설치/시작 버튼. F5에 Service/SensorHost/Setup 준비 |
| 정책 전체가 빨간색 | INotifyDataErrorInfo.GetErrors가 propertyName을 무시하고 전체 오류 반환 | Domain 필드별 검증 결과와 VM 필드 매핑, 의존 필드 통지, 요약 목록. 실제 WPF 입력 오류 생성·해제 시험 |
| 정책 저장 불가 | 실제 서비스 계정으로 DirectoryGuards가 사용자 폴더의 상위 경로 속성을 조회할 수 없음. 기존 권한 도구는 관리 루트만 부여 | 기존 CI의 실제 UI Save에서 Access denied 재현. 부모는 비상속 RA/X만, 관리 루트는 M. Grant access UI와 상세 오류 추가 |
| 정상 실측이 사라짐 | Recovery/History 등 부가 조회 실패도 Dashboard 전체 연결 실패로 처리 | Dashboard 수신 이후의 부가 조회 오류 격리. 단위 시험에서 CPU/볼륨 유지 확인 |
| CPU 카운터 환경 차이 | PDH 오류 시 전체 수집 중단 또는 실패 카운터 조용히 생략 | native GetSystemTimes CPU fallback, RAM 독립 측정, PDH 실패 상태 명시. 온도 지원 여부는 실제 센서 상태로 표시 |

정책 저장은 반환 version을 반영하고 저장 중 추가 편집과 이전 poll 응답을 구분한다. ComboBox의 빈 전이가 원래 볼륨 ID를 지우지 않는다. 드라이브 조회는 이동식 로컬 볼륨과 조회 실패 항목도 표시한다. NTFS·핸들·reparse·삭제 hash 검증은 유지한다.

macOS 공유 코드 보조 시험108건과 Framework4.8/C#7.3 의미 컴파일을 통과했다. Windows 새 동작 시험은 예시 클라이언트를 쓰지 않고 실제 WPF 컨트롤과 SCM/인증 IPC/가상 서비스 계정을 사용한다. 최종 실행 결과는 완료 보고서에 기록한다.

## 1.0.2 재점검

이전 검증은 신규 설치와 한 정책의 성공 경로에 치우쳤다. 사용자의 재발 보고로 설치된 구버전, 실행 중인 설치본, 저장된 정책의 숫자 변환 오류를 포함하도록 확대했다.

| 확인된 결함 | 수정 |
|---|---|
| Dashboard 버전이 1.0.0 상수여서 구버전 서비스 식별 불가 | 공유 assembly metadata를 반환하고 UI에 두 버전과 업데이트 안내 표시 |
| 실행 중 Desktop 파일을 설치 프로그램이 바로 덮어씀 | 설치 경로가 일치하는 Desktop 종료, 서비스 종료 대기, 사전 staging/backup, 실패 복원 |
| asInvoker로 실행한 UI의 모든 서비스 명령이 관리자 인증에서 거부됨 | WPF manifest가 시작 시 관리자 토큰 요청 |
| 3초 polling마다 저장된 PolicyEditor를 교체하여 변환 실패 텍스트/입력 상태 초기화 | ID/version reconciliation으로 동일 버전 editor 유지 |
| 선택한 정책만 IsDirty 검사하여 다른 미저장 초안 유실 | 모든 새/dirty editor 유지, 정책 삭제도 선택한 editor만 제거 |
| History를 최초 한 번만 조회해 작업 후 결과가 비어 보임 | 실행 상태/캐시 변화 및 History 화면의 주기적 갱신 |
| 설치 창이 닫히면 실패 원인 유실 | 설치 transcript와 회전 Desktop 로그, Diagnostics logs 버튼 |

1차 Windows 검증의 삭제 workflow는 시험 threshold를 실제 볼륨보다 크게 설정해 보호 규칙에 차단됐다. 보호를 완화하지 않고 유효한 시험 threshold로 수정했다. 운영 PC의 정확한 재발 오류는 아직 제공되지 않았으므로 이 분석은 코드에서 확인한 결함과 Windows 재현 결과에 한정한다.

추가한 실제 배포 시험에서 신규 Setup.cmd의 `Start-Process -Wait`가 설치 프로그램뿐 아니라 실행된 Desktop의 종료까지 기다리는 문제가 확인됐다. 설치 프로세스 자체의 `WaitForExit`로 바꿨다. 기존 Uninstall.ps1도 Desktop을 Stop-Process한 직후 파일을 지워 잠금 오류가 발생했으므로, 정확한 설치 경로의 Desktop 종료를 확인한 뒤 제거한다. 이 두 결과는 단위 시험만으로는 드러나지 않았고 실행 중 설치본/배포 ZIP 시험으로 확인했다.
