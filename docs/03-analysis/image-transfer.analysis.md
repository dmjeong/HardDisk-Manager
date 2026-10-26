# Image Transfer 구현 대조와 검증

2026-10-26 · WPF / .NET Framework 4.8 / x64 / C#7.3

## 요구사항 대조

| 항목 | 구현과 확인 근거 |
|---|---|
| IT-01 서버·경로 | FTP/Explicit·Implicit FTPS, 별도 DPAPI secret, 경로/문자 인코딩/설정 version 검사, 실제 업로드 시험, 원본 범위 중첩 금지 |
| IT-02 감지·안정화 | FileSystemWatcher + 주기 전체 탐색 + overflow dirty-root, metadata 안정화와 FILE_SHARE_READ native lease, 이미지 signature, 동일 metadata 내용 재검사 |
| IT-03 영속 대기열 | manager.db schema2, SQLite backup migration, 트랜잭션 claim/lease·세대 fencing·시도·증빙, 재시작 후 원격 재조정, 상태/바이트 trigger 집계 |
| IT-04 전송 | FluentFTP55.1.0 Framework assembly, binary/passive, STOR/APPE 고정 버퍼, 실제 최종226/250, staging/동일 폴더 rename/direct write |
| IT-05 검증·충돌 | SHA-256 또는 RETR read-back, 크기 검증 수준 분리, 기존 경로 보류, 550을 부재로 추측하지 않음, RNTO 응답 유실/소비 결과 불명 보류 |
| IT-06 삭제 보호 | 감지 이전 경로 guard, 삭제 직전 RAM hash와 게시 증빙 비교, 같은 DB commit permit과 native 삭제 핸들 close 직렬화, Pause/Archive/설정 축소 후 이전 guard 유지 |
| IT-07 부하·Interlock | 물리 디스크 lane/읽기 제한, 전역 upload/read-back 제한, worker/endpoint 동시성, priority+aging, jitter retry/breaker, 리소스/감사/복구 중단 |
| IT-08 UI·감사 | 독립 Transfer VM와 View, 4개 내부 화면, 편집 보존/명시적 Reload/낙관 version, 비동기 시험·Preview,100건 cursor page/CSV/상세·시도·증빙, Transfer Alerts |
| IT-09 기존 TCP | 수신 framing·파일명·응답 명세가 제공되지 않아 adapter를 임의로 구현하지 않음. 현재 설정에는 FTP/FTPS만 노출 |

## 자동 시험

신규 시험은 전송 상태/보호/DB27건, 실제 FTP control/data socket10건, Windows native8건, Presentation8건으로 구성한다. 기존185건의 삭제·복구·폴더 완료·Interlock·IPC·MVVM 회귀도 함께 실행한다.

- 내용 손상·부정적 최종 FTP 응답·읽기/목록 권한 부족·기존 파일 충돌에서 게시 또는 삭제가 허용되지 않는다.
- 동일 metadata 내용 변경, 이전 worker가 세대 교체 뒤 완료/해제, 중복 이벤트/동시 claim, Pause/Archive/확장자 제거가 보호를 풀지 않는지 확인한다.
- 원격 prefix hash 일치/불일치, APPE 검증, RNTO 적용 뒤 응답/파일 유실, 원본 없는 서비스 재시작 조정을 시험한다.
- SQLite v1 백업과 v2 전환, 신버전 거부, 집계 바이트 변경·완료·세대 교체·재시작, 과도한 설정과 null 확장자 입력을 확인한다.
- Windows에서 쓰기/rename 차단과 기존 writer 거부, 실제 백그라운드 감지·업로드·삭제 증빙·RAM 복구, 재시작 보호, DPAPI/권한과 비동기 IPC를 시험한다. FTPS는 임시 신뢰 인증서로 제어/데이터 채널을 시험한다.
- UI는 조회 중 초안·유효하지 않은 시간 입력 보존, 저장 응답과 새 편집 경합, 구버전 조회 응답, 비밀번호 분리, 비동기 제어, Reload를 확인한다.

구현 코드 `a238936`의 [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/40295863609)는238/238건 통과, 실제/예시·기본/최소 크기의 WPF45장과 바인딩 오류0건을 확인했다. 세부 결과는 [완료 보고서](../04-report/image-transfer.report.md)에 기록했다. 공유 코드의 macOS 보조 시험은75건 통과했으며 제품 Windows/Framework 시험을 대체하지 않는다.

## 구현 시 선택과 한계

별도 완료 marker/manifest, server agent/업무 ACK, spool 파일, 다중 서버 복제, 이미지 변환은 포함하지 않는다. 안정화와 읽기 lease는 현재 파일 한 세대를 안전하게 읽는 수단이며 외부 생산기가 과거 버전을 덮어쓴 뒤 복구하는 기능은 없다.

read lease와 디스크 lane은 원격 검증/게시가 끝나거나 attempt가 취소될 때까지 유지한다. 저속 네트워크에서는 같은 파일의 쓰기와 같은 디스크 작업이 그동안 대기할 수 있다. source가 불변인 생산 규칙과 timeout/속도 설정을 실제 장비에서 확인한다. session은 파일 attempt마다 새로 만든다.

보호 revision은 자동 완화하지 않는다. 보호된 원본·서버·최종 목적지는 고정하며 보호 해제나 동일 원본 목적지 이관 기능은 제공하지 않는다. 등록을 줄이거나 Archive하면 기존 미전송 파일이 계속 보호될 수 있다. 운영 매뉴얼에서 이를 명시한다.

FTP의 SIZE/목록/rename에는 서버 전체에 걸친 조건부 생성/원자적 no-overwrite 계약이 없다. 운영자가 장비별 독립 원격 경로나 고유 이름을 확보해야 한다. 수신기가 즉시 소비하여 결과를 조회할 수 없고 게시 응답까지 잃으면 PublicationUnknown을 유지한다. 이미 게시된 원격 파일의 이후 보관·업무 처리를 확인하지 않는다.

실제 상위 서버 주소·계정·폴더·명명/감시 규칙을 제공받지 않아 운영 수신기와의 실연결은 수행하지 않았다. Implicit FTPS/CP949/서버별 rename·APPE 지원,100만 파일·다중 HDD 처리량과72시간 운전은 설치 환경에서 확인할 인수 항목이다.
