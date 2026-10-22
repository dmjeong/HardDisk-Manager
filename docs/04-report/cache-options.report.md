# Recovery cache options report

Settings → Recovery cache → RAM limit (MiB)에서 보관 한도를 지정하고 Save changes로 적용합니다. 기본값 1024 MiB (1 GiB), 범위 1~16384 MiB (16 GiB)입니다. 삭제·Preview·복구가 진행 중이면 용량 변경을 거부하며 작업 완료 또는 취소 후 다시 저장합니다. 저장값은 다음 서비스 시작에도 유지합니다.

용량 증가는 기존 복구본과 캐시 세대를 유지하며 메모리를 미리 전부 할당하지 않습니다. 축소는 변경 후의 메타데이터/파일 블록 예산에 맞춰 오래된 복구본부터 퇴출하고 비어 있는 블록 풀을 줄입니다. 보관 한도는 전체 프로세스 메모리와 다르며 OS가 표시하는 메모리 감소 시점은 GC에 따라 달라집니다.

핀·준비·복구 중 항목 때문에 축소가 불가능하면 부분 퇴출 없이 거부합니다. 설정+감사 기록 저장이 실패하면 캐시 상태를 변경하지 않습니다. 퇴출 감사 콜백 실패로 저장값과 실행값이 달라지는 경우 이후 정리를 차단하며 Resume로 이 검사를 우회할 수 없습니다. 기존 파일별 DeleteIntent, RAM 무결성 검사와 폴더 정리 규칙을 유지합니다.

UI는 MiB 정수 범위를 검증하고 저장 실패 시 입력 초안을 유지합니다. 성공 시 이미 진행 중이던 이전 조회가 끝난 후 Dashboard/Recovery를 다시 요청해 실제 용량과 남은 복구본을 표시합니다. 다른 환경설정의 편집 중 값은 덮어쓰지 않습니다. 고정 1 GiB로 표시하던 Recovery/확인 문구도 수정했습니다.

검증 코드 커밋은 `9f607f0b45e95f783037e184273cb5374f86b14e`입니다. [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/39868712416)에서 112개 테스트를 모두 통과했습니다. 새 17개 사례는 캐시 8개, Presentation 5개, 서비스 3개, 설정·감사 트랜잭션 1개입니다. 실제 서비스 설치·인증 IPC·제거, Visual Studio WPF 참조 평가, WPF 화면 29장도 통과했습니다. 바인딩 오류는 없었고 Settings 기본/최소 크기를 시각 확인했습니다.

패키지: CI ExplorerManager-win-x64 artifact / 로컬 `artifacts/ExplorerManager-win-x64.zip`. 화면: [Settings](../images/settings.png).

사용자가 요청한 추가 옵션 검토는 [운영 옵션 제안](../01-plan/features/operator-options.plan.md)에 정리했습니다. 최소 여유 RAM, 신규 잠금 알림 빈도, 실행 요일·분 단위 시간, 이력 용량 상한을 우선 권장하며, 이 후보들은 이번에 추가 구현하지 않았습니다.
