# Explorer Manager 구현과 검증 현황

핵심 기능, WPF, Windows Service, SensorHost, 로컬 인증 IPC, SQLite 이력, 설치/제거, CI를 구현했습니다. 솔루션의 앱·라이브러리·시험을 모두 기존 .NET Framework 4.8 프로젝트로 통일하고 .NET 8 SDK 고정과 멀티타깃을 제거했습니다. 전체 자동 시험은 44개이며 Windows에서 모두 실행합니다. .NET SDK 경로를 비활성화한 빌드, WPF 디자인 시점 평가, 설치된 서비스와 WPF 7개 화면의 실제 통신·렌더링·제거 연동 시험을 수행합니다. 최신 결과는 저장소 Actions의 해당 커밋을 기준으로 확인합니다.

대상은 로컬 NTFS 일반 파일입니다. 센서 지원 범위는 장비와 권한에 따라 달라집니다. 코드 서명 인증서, 실제 운영 장비의 센서 정확도, 72시간 연속 운전 및 100만 파일·16볼륨 현장 검증은 배포 환경에서 확인할 항목입니다.

설계와 달라진 배포 선택: 최초 버전의 UI와 Pipe 변경 명령은 관리자 운영자에게만 개방합니다. 일반 사용자 Viewer UI는 제공하지 않습니다. 성능 표본은 분 단위 7일을 유지하며 장기 시간 단위 집계는 후속 확장 항목입니다. 별도 MSI 대신 관리자 PowerShell 설치 패키지를 제공합니다.

세부 자료: [요구사항 대응과 검증](03-analysis/explorer-manager.analysis.md), [구현 결과](04-report/explorer-manager.report.md).
