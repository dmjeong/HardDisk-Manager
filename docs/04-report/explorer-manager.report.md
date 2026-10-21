# Explorer Manager 구현 결과

2026-10-21 · v1.0.0 · 저장소 기본 브랜치 main

여러 로컬 NTFS 드라이브의 보관 기간·용량 기반 정리, 전역 RAM 복구, 삭제 이력, 잠금 알람, 승인한 앱 종료, 자원 및 디스크 이상 모니터링을 구현했다.

## 제공 파일

- ExplorerManager.sln: net48/x64 애플리케이션과 공용 로직·시험 프로젝트.
- ExplorerManager.Desktop: WPF/MVVM 정책·대시보드·이력·복구·모니터링·알람.
- ExplorerManager.Service: 전용 가상 계정으로 실행하는 삭제·스케줄·감사·캐시 호스트.
- ExplorerManager.SensorHost: LocalSystem의 읽기 전용 센서 서비스와 제한 권한 대체 실행.
- scripts: 빌드, 관리자 설치, 관리 루트 권한 부여, 제거, 연동 검증.
- .github/workflows/build.yml: portable 시험과 Windows 빌드·NTFS·설치·WPF 검증 및 배포 ZIP.
- licenses: 실제 패키지의 라이선스 고지와 버전 목록.

## 삭제와 복구

후보 목록 전체를 확인한 뒤 오래된 순서로 처리한다. 등록 루트·최종 경로·파일 ID·속성·시간을 재검증하고, 동일 핸들에서 복구본을 복사·검증한 후 삭제한다. 목표 공간 확보, 정책 변경, 일시정지, 시간·항목·바이트 상한을 검사한다. 감사 저장에 실패하면 신규 삭제를 중단한다.

모든 드라이브가 공유하는 1GiB 캐시는 FIFO와 복구 중 pin을 지원한다. 큰 파일과 지원하지 않는 특수 파일은 스킵한다. 서비스 재시작 후 복구본은 사라진다. 복구는 본문 해시와 지원 메타데이터를 확인하고 기존 파일을 덮어쓰지 않으며 재삭제 보호를 적용한다.

## 검증과 설치

전체 net48 빌드와 자동 시험 44개, 설치된 서비스/IPC/WPF 렌더/제거 연동 시험을 구성했다. 통과 기록과 실행 패키지는 [Actions](https://github.com/dmjeong/HardDisk-Manager/actions)에 저장한다. 상세 대응과 현장 확인 항목은 [검증 분석](../03-analysis/explorer-manager.analysis.md)을 참조한다.

Windows PowerShell 5.1 관리자 창에서 배포 ZIP의 Install.ps1을 실행한다. 그다음 Grant-ManagedRoot.ps1로 서비스 권한을 부여하고 정책을 저장·미리보기·활성화한다. 설치 직후 자동 삭제는 꺼져 있다.

## 운영상 한계

센서 지원은 장비·드라이버·보안 정책에 따라 달라지며 N/A를 명시한다. RAM 캐시 퇴출과 재시작 이후에는 복구할 수 없다. NAS/UNC·ReFS·재부팅 후 복구·특수 NTFS 항목의 완전 복구는 제공하지 않는다. 코드 서명과 실제 장비의 장시간·대규모 운전 검증은 배포 환경에서 수행한다.

관련 문서: [README](../../README.md), [운영 매뉴얼](../operations.md), [개발 계획](../01-plan/features/explorer-manager.plan.md), [기술 설계 원안](../02-design/features/explorer-manager.design.md).
