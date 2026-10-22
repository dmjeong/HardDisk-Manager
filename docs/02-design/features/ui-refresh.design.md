# Explorer Manager UI 디자인

## 참고
- https://linear.app/now/behind-the-latest-design-refresh (2026-03-12)
- https://vercel.com/geist/introduction
- https://fluent2.microsoft.design/layout

## 토큰과 구성
- 차콜 바탕 #141517, 사이드바 #101113, 패널 #1B1C20, 입력 #17181B, 경계 #2B2D33.
- 주 텍스트 #F2F2F5, 보조 #A0A2AE, 강조 #8175F5, 성공 #64C8A7, 경고 #E8B56D, 위험 #EC858D.
- 간격 4/8/12/16/24/32, 모서리 6/8/12, 본문 13, 화면 제목 24, 주요 수치 28.
- 공용 ResourceDictionary에서 버튼/입력/선택/탭/표/카드/빈 상태/포커스/비활성 상태를 정의한다.
- 네이티브 창 프레임과 키보드 탐색 유지. 최소 창 1120x720, 기본 1440x920. 긴 화면은 스크롤한다.
- TabControl의 선택 모델을 유지한 커스텀 좌측 탐색으로 코드 비하인드의 업무 로직을 추가하지 않는다.

## 화면
1. 개요: 4개 수치, N개 드라이브 카드, CPU 추이/센서 상태, 실행 기록.
2. 정책: 정책 목록 + 기본/기준/제외/고급의 세로 편집 화면, 고정 작업 막대. 선택 없음은 안내 표시.
3. 삭제 기록: 검색/CSV/페이지 이동, 항목별 표, 폴더 집계.
4. 복구: RAM 제한 설명/캐시 사용량, 원래/다른 경로 복구, 폴더 복구, 복구 목록.
5. 리소스: CPU/온도/디스크 수치, 시간축/단위가 표시된 I/O 차트, 센서 상세.
6. 알림: 선택 항목 상세와 확인/재시도/종료 작업, 전체 목록.
7. 설정: 기록 보관/센서 기준/종료 허용 앱/백그라운드 안내.

## 데이터와 MVVM
- ViewModel이 실제 모델을 집계하고 속성 변경 알림을 보낸다. 미지원/미수신 데이터를 0으로 보이지 않는다.
- 뷰의 converter는 용량, 로컬 시간, 상태 등 순수 표시 변환만 한다.
- CPU와 디스크 차트는 데이터가 없는 구간을 연결하지 않고 측정 대기를 표시한다. 디스크 자동 축은 단위와 상한을 표시한다.
- 경로는 줄임표와 전체 경로 툴팁, 표는 가상화와 가로 스크롤 유지.
- 위험 작업은 기존 확인 절차와 서비스 측 안전 검증을 유지한다.

## UI 문구 원칙
- 메뉴는 Dashboard / Policies / History / Recovery / Monitoring / Alerts / Settings로 통일한다.
- CPU usage, Read/Write throughput, I/O latency, Recovery cache, Preview, Refresh 등 기술 용어와 짧은 UI 레이블은 영어가 자연스러우면 영어를 사용한다. 설명·빈 상태·삭제/복구/강제 종료 확인 문장은 한국어로 명확하게 작성한다.
- 문장형 대소문자(sentence case)를 사용한다. 사용자 지정 정책 이름과 경로는 번역하지 않는다.
- Enabled policies는 저장된 활성화 설정, Pending alerts는 미확인 상태를 뜻한다. Free space Δ는 외부 I/O도 반영하는 여유 공간 순변화로 실제 확보 용량과 같다고 표현하지 않는다.
- I/O latency는 읽기·쓰기 평균 지연 중 큰 값이며 개별 I/O의 최대 지연으로 표현하지 않는다. RAM cache는 Recycle Bin이나 영구 Backup으로 부르지 않는다.
