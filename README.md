# Explorer Manager

여러 NTFS 드라이브를 감시하고 용량 기준을 넘으면 보관 기간이 지난 파일을 오래된 순서로 정리하는 Windows 프로그램입니다.

## 실행 구성

- WPF · .NET Framework 4.8 · x64 · MVVM
- Windows Service: 스케줄, 삭제, 감사 이력, 전역 1GiB 복구 캐시
- Desktop: 정책, 미리보기, 실행 상태, 이력/CSV, 복구, 알람, 트레이
- SensorHost: CPU/디스크 센서 수집과 장애 격리. 기본 설치는 읽기 전용 LocalSystem 센서 서비스도 등록
- Named Pipe: 관리자 인증, 로컬 전용, 요청 중복 실행 방지
- SQLite: 정책·삭제 저널·알람·성능 집계

## 설치

Windows 11 x64 또는 Windows Server 2022/2025 Desktop Experience와 .NET Framework 4.8 이상이 필요합니다.

1. [Actions](https://github.com/dmjeong/HardDisk-Manager/actions)에서 성공한 실행의 `ExplorerManager-win-x64` artifact를 받습니다.
2. 그 안의 `ExplorerManager-win-x64.zip`을 완전히 압축 해제합니다.
3. 관리자 PowerShell에서 `./Install.ps1`을 실행합니다.
4. 관리할 폴더에 서비스 권한을 부여합니다: `./Grant-ManagedRoot.ps1 -Path 'D:\Archive'`.
5. WPF 앱을 관리자 권한으로 실행하고 **정책 저장 → 미리보기 → 결과 확인 → 자동 삭제 활성화** 순서로 설정합니다.

삭제 서비스 이름은 `ExplorerManager`(전용 가상 계정), 하드웨어 센서 서비스는 `ExplorerManagerSensors`(LocalSystem)입니다. 센서 서비스는 측정값만 제공하며 삭제·프로세스 종료 명령을 받지 않습니다. `Install.ps1 -NoHardwareSensors`로 관리자 센서를 제외할 수 있습니다.

데이터는 `%ProgramData%\ExplorerManager\Data`입니다. 창을 닫으면 트레이로 이동하며, 트레이에서 화면을 종료해도 서비스는 계속 실행됩니다. 기본 설치는 설치한 운영자 계정의 로그인 시 UI를 관리자 권한으로 실행하는 작업을 등록합니다.

자동 삭제는 설치 직후 비활성 상태입니다. 수동 정리도 용량·기간·보호 규칙을 그대로 적용합니다. NAS/UNC, USB 자동 정리, ReFS는 v1 대상에 포함하지 않습니다.

## 삭제 규칙

볼륨별로 여러 관리 루트를 등록할 수 있습니다. 사용률(예: 90% 시작/85% 중단), 사용 바이트, 최소 여유 바이트 중 한 가지를 선택합니다. 같은 볼륨의 전체 후보를 시각 순서로 정렬하고 매 항목 전후에 실제 여유 공간을 다시 측정합니다.

기본 보관 기준은 마지막 수정 시각 30일이며 생성 시각으로 변경할 수 있습니다. 최근 파일·제외 경로·복구 보호 대상은 유지합니다. 빈 폴더만 삭제하며 등록 루트는 삭제하지 않습니다. 경로와 파일 ID를 확인한 동일 핸들에서 복사·삭제하여 교체 경합을 차단합니다.

링크, junction, reparse point, 다중 hard link, ADS, 암호화·압축·sparse·숨김·시스템·읽기 전용 파일 등 미지원 특수 항목은 스킵합니다. 전체 스캔을 완료하지 못하면 부분 목록으로 삭제하지 않습니다.

## 1GiB 복구

삭제 전에 파일 전체를 서비스 메모리에 복사하고 SHA-256을 검증합니다. 모든 드라이브가 하나의 1GiB 예산을 공유하며 메타데이터와 블록 비용도 포함합니다. 단일 파일이 예산보다 크면 스킵합니다.

최근 복구본만 FIFO로 유지합니다. 이전 복구본 퇴출, 서비스 재시작/중지, PC 종료 뒤에는 복구할 수 없습니다. WPF 창만 닫는 경우에는 캐시가 유지됩니다. 파일 본문을 복구용 디스크 파일로 보관하지 않지만 일반 메모리는 OS 페이지 파일 등에 기록될 수 있습니다.

복구는 기존 파일을 덮어쓰지 않고 충돌 시 새 이름을 사용합니다. 본문·생성/수정 시각·지원 DACL을 복원하고 24시간 재삭제 보호를 적용합니다. 원래 소유자·감사 ACL 등 모든 NTFS 메타데이터를 완전 재현하는 기능은 제공하지 않습니다.

## 잠금과 강제 종료

잠금 파일은 스킵하고 이력과 알람을 남깁니다. 알람에는 **강제 종료 후 삭제하시겠습니까?**를 표시합니다. 종료는 환경설정의 허용 앱 중 현재 운영자 소유·동일 세션의 일반 앱만 지원합니다. 프로세스 시작 시각과 실제 핸들을 확인하며 필수·보호·서비스 프로세스는 제외합니다. 권한 거부와 잠금을 구분합니다.

## 모니터링

CPU 사용률, 가용 RAM, 볼륨 용량, 디스크 읽기·쓰기 처리량/IOPS/지연/큐, 지원 온도·건강 지표, Windows 저장장치 오류를 수집합니다. 센서 미지원·권한 부족은 N/A 상태로 표시합니다. 온도 임계값은 장비에 맞게 설정해야 합니다. 유휴 상태의 0B/s를 장애로 판정하지 않습니다.

센서 드라이버가 보안 정책에 의해 차단되면 해당 센서를 사용할 수 없습니다. OS 보안 설정을 자동으로 낮추지 않습니다.

## 빌드와 검증

Windows에서 Visual Studio 2022 Build Tools의 .NET 데스크톱 개발 구성과 .NET Framework 4.8 개발 팩, .NET 8 SDK가 필요합니다. 개발자 PowerShell에서:

```powershell
./scripts/Build.ps1
dotnet test tests/ExplorerManager.Tests/ExplorerManager.Tests.csproj -f net48 -c Release --no-build
```

순수 정책/캐시/삭제 조정/감사 로직은 macOS·Linux에서도 검증할 수 있습니다:

```sh
dotnet test tests/ExplorerManager.Tests/ExplorerManager.Tests.csproj -f net8.0 -p:NoWarn=CA1416
```

배포 프로그램의 대상은 net48입니다. net8.0은 동일 소스의 휴대 가능한 자동 시험용 대상입니다. CI는 WPF와 서비스를 빌드하고 실제 NTFS 삭제·복구, 서비스 설치·IPC·WPF 렌더·제거 시험을 실행합니다. 실행 패키지는 `artifacts/ExplorerManager-win-x64.zip`입니다. 코드 서명은 `Build.ps1 -CertificateThumbprint ...`로 보유 인증서를 지정할 때 적용됩니다. 기본 CI 패키지는 서명되지 않습니다.

## 운영과 문서

- [개발 계획](docs/01-plan/features/explorer-manager.plan.md)
- [기술 설계](docs/02-design/features/explorer-manager.design.md)
- [운영 매뉴얼](docs/operations.md)
- [구현·검증 현황](docs/implementation-status.md)
- [제3자 라이선스](THIRD-PARTY-NOTICES.md)

`Uninstall.ps1`은 서비스를 중지하고 프로그램을 제거하며 정책·이력은 보존합니다. 관리 대상 폴더를 제거하지 않습니다. 서비스 업데이트/중지는 RAM 복구본을 소멸시킵니다.
