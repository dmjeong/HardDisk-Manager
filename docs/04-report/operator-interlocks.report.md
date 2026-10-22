# Operator options and interlocks report

사용자가 승인한 네 가지 우선순위를 모두 구현하고 내부 리소스·실행 중 앱 Interlock을 추가했습니다.

| 옵션 | 제공 기능 |
| --- | --- |
| 최소 여유 RAM | Settings RAM reserve (MiB), 기본1024 MiB, 부족 감지 즉시 차단 |
| 알림 방식/간격 | Popup / Tray notification / Log only, 기본300초, 여러 잠금 알림 묶기와 누적 팝업 억제, 로그/Alerts 유지 |
| 실행 요일/시간 | 월~일 선택, HH:mm, 종료24:00, 자정 이후 시작 요일 기준, 기존 JSON 호환 |
| 이력 용량 | 기본2048 MiB, 기간+용량 정리, 하향 저장 시 유지 작업, 진행/복구/미해결 기록 보호 |

Interlocks는 CPU·CPU/디스크 온도·활성 I/O 지연의 지속 감지, 즉시 RAM 차단, 지정 앱 실행/조회 불가 차단, 측정 신선도, 디스크 Critical, 감사/캐시 설정 불일치를 표시합니다. 리소스는 안정 회복 대기와 선택적 수동 Reset을 지원하고 래치는 서비스 재시작 뒤에도 유지합니다. 누락/미지원/비정상 측정과 아직 위험한 Pending/Tripped/Recovering 조건으로 Reset할 수 없습니다.

Reset과 수동 Pause/Resume는 별개입니다. Resume는 감사 오류를 해제하지 않으며, 감사 오류 Reset은 실제 durable 감사 쓰기를 확인합니다. Critical 저장장치 알림·프로세스·측정 차단도 Reset으로 우회할 수 없습니다. 감시 중인 작업의 CTS 세대를 확인하는1초 watchdog이 진행 중인 캐시 복사를 취소하고, 엔진은 부분 캐시를 제거하며 원본 보존과 차단 종료 사유를 기록합니다. 파일별 삭제 직전 검증은 그대로 유지합니다. 복구 작업은 별도 실행으로 제공합니다.

모든 설정 변경은 전체 설정 스냅샷과 함께 감사 DB에 기록합니다. 리소스·앱·측정 중단·감사 오류의 차단/해제 및 운영자 Reset을 기록합니다. 보관 상한을 보호 기록 때문에 지킬 수 없으면 HistoryLimitProtected 이벤트를 남깁니다. 외부 PLC/설비 프로토콜은 연결 요구사항을 정의하지 않은 상태이며 현재 구현 범위는 내부 리소스/앱 조건입니다.

검증한 코드: `75931e3b2185397dfc07e9fabba9512f20b0a61d`. [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/39874432958)에서185개 테스트를 모두 실행해 성공했습니다. .NET SDK 경로를 비활성화한 Framework4.8 빌드, 실제 NTFS 파일 보호, 운영 중 앱 차단에 의한 느린 RAM 복사 취소, 실제 SQLite DeleteIntent 오류와 감사 복구, 설정 범위/재시작, Visual Studio WPF 디자인 참조, 서비스 설치·인증 IPC·제거까지 통과했습니다. 실제/예시 데이터와 기본/최소 크기의 WPF 화면29장, 바인딩 오류0건을 확인했습니다.

[Dashboard](../images/overview.png), [Settings](../images/settings.png), [운영 매뉴얼](../operations.md). 패키지는 CI ExplorerManager-win-x64 artifact와 로컬 `artifacts/ExplorerManager-win-x64.zip`에 있습니다.

운영 장비별 센서 지원과 장시간·대규모 HDD 부하 검증은 기존 현장 인수 범위로 유지합니다. 중단/해제 판단은 측정 주기와 설정한 지속/회복 시간에 따릅니다.
