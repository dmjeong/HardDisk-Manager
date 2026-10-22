# Explorer Manager UI 개편 결과

좌측 탐색과 차콜 테마를 공용 XAML 디자인 시스템으로 구현했다. 화면별 정보 구조를 재배치하고 드라이브 카드, 요약 수치, 그래프, 구역별 정책 편집, 빈 상태와 선택 항목 상세를 추가했다.

표시 로직은 Presentation에 유지하고 View의 converter와 차트는 표현만 담당한다. .NET Framework 4.8/C# 7.3/x64, 기존 프로젝트 형식, 서비스와 삭제·복구 안전 검증을 유지한다. 새로운 UI 런타임 패키지는 추가하지 않았다.

12개 표시·측정 상태 시험 사례를 추가해 기존 44개와 합계 56개다. 실제 Windows 검증 결과와 실행 패키지·전체 캡처는 [CI 실행](https://github.com/dmjeong/HardDisk-Manager/actions/runs/39862785347)에서 제공한다. 캡처의 예시 데이터는 실제 운영 기록이나 센서 값이 아니다.

- [계획](../01-plan/features/ui-refresh.plan.md)
- [디자인](../02-design/features/ui-refresh.design.md)
- [검토](../03-analysis/ui-refresh.analysis.md)
