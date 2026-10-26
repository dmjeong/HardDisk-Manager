# Image Transfer 구현 결과

FTP/FTPS로 지정 경로의 이미지를 상위 서버에 전송하는 기능을 Windows Service와 WPF Transfers에 구현했다. 수신 프로그램을 수정하지 않고 기존 서버의 경로·파일명·접속 규칙을 사용한다. 제품 전체는 .NET Framework4.8/x64와 MVVM을 유지한다.

## 제공 기능

- Connection/전송 정책/실행 옵션, 별도 비밀번호 입력과 DPAPI 보관, 연결·32 KiB 업로드 시험·Preview.
- 감시+재검색, 안정화/native read lease, 영속 대기열·재시도·검증된 APPE, 재시작 재조정, 우선순위·동시성·속도 제한.
- 임시 업로드·SHA-256 또는 read-back·게시 확인, 기존 파일 충돌/권한 오류/결과 불명 보류.
- 감지 이전부터 미전송 원본 보호, 삭제 직전 현재 hash/증빙 비교와 이력 연결. Pause/Archive 후에도 기존 보호 유지.
- Queue & history, 상태·진행률·바이트·시도·증빙 상세와 CSV, 정책/Connections/Execution options 화면.
- 리소스·감사·디스크 Interlock, 복구 우선 중단, RAM 복구 예산을 사용하지 않는 고정 버퍼 스트리밍.

## 검증

검증한 구현 코드: `a238936`. [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/40295863609)에서 **238/238건 통과**, .NET SDK 해석을 비활성화한 Framework MSBuild, WPF design-time 참조 평가, 서비스 설치·인증 IPC·제거를 모두 통과했다. 기본/최소 크기와 실제/예시 데이터의 WPF 화면 **45장**, 바인딩 오류 **0건**을 확인했다. 실제 FTPS 제어·데이터 채널에서 업로드/read-back/APPE/rename도 통과했다.

[Transfers](../images/transfers.png) · [정책](../images/transfer-policies.png) · [Connections](../images/transfer-connections.png) · [실행 옵션](../images/transfer-options.png). 패키지는 해당 CI의 ExplorerManager-win-x64 artifact와 로컬 `artifacts/ExplorerManager-win-x64.zip`에 있다. [요구사항 대조와 시험 범위](../03-analysis/image-transfer.analysis.md).

## 최초 설정

Transfers → Connections에서 Save connection → Set password → Test connection → 수신기가 감시하지 않는 폴더로 Test upload를 실행한다. Transfer policies에서 경로·게시/검증 방식 → Preview → 수신 규칙/독립 목적지 확인 → Enabled와 Save policy 순서로 적용한다. 자세한 권한/옵션/오류 대응은 [운영 매뉴얼](../operations.md#이미지-전송-설정과-운영)에 있다.

TCP 전용 수신기는 기존 통신 명세가 필요하다. 운영 서버 정보가 제공되지 않아 실서버 접속 시험은 하지 않았으며 실제 수신기의 호환성·HDD 처리량·장시간 시험은 현장 인수 항목이다. FTP 완료는 상위 업무 처리나 미래 보관을 보장하지 않는다.
