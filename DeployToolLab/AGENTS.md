# DeployTool - AI Agent 개발 프롬프트

## 프로젝트 개요

파일 배포 관리 툴. 운영 서버의 파일과 배포 파일을 비교하고,
사람이 검토 후 선택적으로 적용하는 워크플로우 기반 Blazor Server 웹앱.

각 서버마다 독립적으로 실행 (서버 간 방화벽으로 차단된 멀티서버 환경).

---

## 기술 스택

- **Blazor Server** (.NET 8)
- **Microsoft.Data.SqlClient** - DB 스키마 뷰어용 MSSQL 직접 연결
- **DiffPlex** (NuGet) - 인라인 diff 렌더링
- **SignalR** - 실시간 로그 스트리밍
- **Bootstrap 5** - UI 기본 스타일

---

## 전체 화면 구성

```
사이드바
├── 파일 배포 (Step 1 ~ Step 6)
└── DB 스키마 뷰어
```

---

## 파일 배포 워크플로우

### Step 1: 경로 설정

- 운영 경로 입력 (예: `D:\NETS\O365M`)
- 배포 파일 경로 입력 (예: `D:\imprep`)
- [빠른 스캔] 버튼 클릭 → Step 1.5로 이동

### Step 1.5: 배포 범위 선택 (신규)

빠른 스캔은 파일 내용을 읽지 않고 최상위 폴더 목록만 열거 (1~2초 내 완료).

**전체 비교 / 프로젝트 선택** 중 택 1:

```
○ 전체 비교  (예상 파일 수 표시)
  └─ 자동으로 운영 전용 제외 패턴 적용
     (log/, Backup/, temp/, Elastic/, _quarantine/ 등)

● 프로젝트 선택
  ┌────────────────────────────────────────┐
  │ ☑ Batches  (39개 프로젝트)             │
  │   ☑ O365M.Batches.Collectors.EXO      │
  │   ☐ O365M.Batches.SendMail            │
  │   ...                                  │
  │ ☑ Web      (20개 프로젝트)             │
  │   ☑ Nets.M365M.Web.AdminPortal        │
  │   ☐ Nets.IM.Web.API                   │
  │   ...                                  │
  │ ☐ Services                             │
  │ ☑ Config                               │
  └────────────────────────────────────────┘
  예상 대상: 약 N개 파일

[분석 시작]
```

**선택 규칙:**
- 배포 경로에만 존재하는 폴더(신규 프로젝트)는 🆕 뱃지 표시
- 운영 경로에만 존재하는 폴더는 🗑 뱃지 표시
- 전체 비교 선택 시 제외 패턴은 사용자가 편집 가능

### Step 2: 파일 스캔 및 분석

선택된 범위만 재귀 스캔. 파일별 상태 분류:

- 🟢 **추가** - 배포에만 존재
- 🔴 **삭제 예정** - 운영에만 존재
- 🟡 **변경** - 양쪽 존재하나 내용 다름
- ✅ **동일** - 변경 없음

### Step 3: 트리 뷰 기반 검토 UI (핵심)

**좌측: 폴더/파일 트리 뷰**

- 재귀 컴포넌트 (`FileTreeNode.razor`)
- 폴더에 하위 변경사항 집계 뱃지 (🟢2 🔴1 🟡5)
- 폴더 단위 일괄 처리 가능
- 파일 단위 개별 처리 가능
- "변경사항만 보기 / 전체 보기" 필터 토글

**우측: 선택 항목에 따라 패널 변경**

- **폴더 선택 시**: 하위 변경사항 요약 + 일괄 적용 버튼
- **DLL 등 바이너리 선택 시**: 파일 정보(크기/날짜/버전) + [적용] [스킵] 버튼
  - DLL은 `FileVersionInfo`로 AssemblyVersion, FileVersion 비교
  - 버전이 낮아지는 다운그레이드 감지 시 ⚠️ 경고 표시
  - `*.pdb` 파일은 DLL과 세트로 묶어 함께 처리
- **텍스트 파일 선택 시**: 인라인 Diff 뷰 + [운영 유지] [배포 적용] 버튼

### Step 4: 백업

- 배포 실행 전 운영 폴더(선택 범위) 타임스탬프 기반 백업 폴더에 자동 복사
- 백업 경로 표시 및 확인

### Step 5: 배포 실행

- Step 3에서 결정된 사항 기반으로 실제 파일 적용
- 삭제 예정 파일은 즉시 삭제하지 않고 `_quarantine\` 폴더로 이동
- 실시간 로그 스트리밍 (SignalR)
- 진행률 표시
- 배포 전 Pre-flight Check 자동 실행:
  - 운영 폴더 쓰기 권한 확인
  - 백업 경로 용량 확인
  - 배포 대상 DLL 중 프로세스가 점유 중인 파일 감지 (파일 잠금 확인)
  - 동시 배포 세션 충돌 감지 (세션 락)
  - 체크 실패 항목 있으면 경고 표시 후 사용자 확인 필요

### Step 6: 결과 확인

- 성공/실패/스킵 파일 목록
- 롤백 버튼 (백업 폴더로 복원)
- `_quarantine` 폴더 정리 버튼

---

## 파일별 처리 규칙

### DLL 및 바이너리

- 파일명 / 크기 / 수정일자 / FileVersion / AssemblyVersion 비교
- 다운그레이드(버전이 낮아지는 경우) 감지 시 ⚠️ 경고
- `*.pdb` 파일은 DLL과 세트 처리
- 사람이 적용 여부 최종 결정

### appsettings.json

- JSON 파싱 후 키 단위 diff
- 운영에만 있는 키: 별도 표시 (보존 권장)
- 배포에만 있는 키: 추가 권장
- 민감한 키 패턴 (`*ConnectionString*`, `*Password*`, `*Secret*`) 자동 감지 → 운영값 우선 유지 권장
- 환경별 파일 분리 처리:
  - 배포 환경 선택 (Development / Release / Production)
  - `appsettings.Development.json`은 운영 배포에서 자동 제외 권장 표시
- 사람이 최종 결정

### web.config / .config 파일

- XML 형식
- 라인 단위 diff 표시 (DiffPlex 사용)
- 의미론적 자동 merge 불가, 사람이 판단
- [운영 유지] [배포 적용] 선택

---

## 운영 전용 제외 패턴 (기본값)

전체 비교 모드에서 자동 제외되는 패턴. 사용자가 편집 가능.

```
log/
Backup/
temp/
_quarantine/
Elastic/
*.log
*.rb
*.jar
*.gemspec
Config-Copy*/
```

---

## DB 스키마 뷰어

파일 배포와 독립된 별도 메뉴. 운영 중인 MSSQL DB의 스키마를 확인하는 화면.

### 연결 설정

- 연결 문자열 직접 입력 (평문)
- 세션 메모리에만 보관 (파일 저장 없음, 페이지 새로고침/세션 종료 시 초기화)
- [연결 테스트] → 성공/실패 즉시 표시
- [연결] → 스키마 로드

```
연결 문자열: [ Data Source=10.99.50.1;Initial Catalog=o365m_wiki;User ID=...;Pwd=... ]

[연결 테스트]  [연결]
```

### 스키마 뷰어 UI

**좌측: 객체 트리**

```
🗄 o365m_wiki
├─ 📁 Tables (N개)
│   ├─ IM_User
│   ├─ IM_Group
│   └─ ...
├─ 📁 Views (N개)
└─ 📁 Stored Procedures (N개)
```

- 테이블명/뷰명/SP명 검색 필터

**우측: 선택 항목 상세**

- **테이블 선택 시**:
  - 컬럼 목록 (이름 / 데이터 타입 / Null 여부 / PK 🔑 / FK 🔗 표시)
  - 인덱스 목록
  - FK 관계 표시 (참조 테이블명)
- **뷰 선택 시**: 뷰 정의 SQL
- **SP 선택 시**: SP 정의 SQL

### 구현 방식

`Microsoft.Data.SqlClient`로 `INFORMATION_SCHEMA` 시스템 카탈로그 직접 쿼리.
EF Core DbContext는 사용하지 않음.

```sql
-- 테이블/뷰 목록
SELECT TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES

-- 컬럼 정보
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS

-- PK/FK 제약조건
SELECT * FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
SELECT * FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
```

---

## 제약 및 주의사항

- XML 의미론적 자동 merge 불가 → 라인 diff만 제공
- DLL 내용 diff 불가 → 메타정보(크기/날짜/버전)만 비교
- 동시 배포 작업 방지를 위한 세션 락 처리
- 인코딩은 UTF-8 기준 (EUC-KR 혼재 가능성 고려)
- DB 연결 문자열은 세션 메모리에만 보관, 디스크 저장 없음

---

## 프로젝트 구조

```
DeployTool/
├── AGENTS.md
├── Pages/
│   ├── Step1_PathConfig.razor        # 경로 입력
│   ├── Step1_5_ScopeSelect.razor     # 배포 범위 선택 (신규)
│   ├── Step2_Scan.razor              # 파일 스캔
│   ├── Step3_Review.razor            # 검토 UI
│   ├── Step4_Backup.razor            # 백업
│   ├── Step5_Deploy.razor            # 배포 실행
│   ├── Step6_Result.razor            # 결과 확인
│   └── DbSchema.razor                # DB 스키마 뷰어 (신규)
├── Components/
│   ├── FileTreeNode.razor            # 재귀 트리 컴포넌트
│   ├── DiffViewer.razor              # 인라인 diff 뷰
│   ├── DeployLog.razor               # 실시간 로그
│   ├── DbTableTree.razor             # DB 객체 트리 (신규)
│   └── DbTableDetail.razor           # DB 객체 상세 (신규)
├── Services/
│   ├── FileScanner.cs                # 변경사항 스캔
│   ├── DiffEngine.cs                 # diff 계산
│   ├── XmlDiffService.cs             # XML 라인 diff
│   ├── JsonDiffService.cs            # JSON 키 단위 diff
│   ├── DeployExecutor.cs             # 실제 파일 적용
│   ├── BackupService.cs              # 백업/롤백
│   ├── PreflightChecker.cs           # 배포 전 검증 (신규)
│   └── DbSchemaService.cs            # DB 스키마 쿼리 (신규)
└── Models/
    ├── DeploySession.cs              # 전체 세션 상태
    ├── FileChangeItem.cs             # 파일별 변경 정보
    ├── DeployDecision.cs             # 사용자 결정 사항
    ├── ScopeSelection.cs             # 배포 범위 선택 (신규)
    └── DbSchemaModels.cs             # 테이블/컬럼/인덱스 모델 (신규)
```

---

## 코딩 규칙

- .NET 10 사용
- `async/await` 철저히 적용 (파일 I/O, DB 쿼리 전부 비동기)
- Blazor Server 상태 관리는 Scoped 서비스로 처리
- SignalR Hub는 배포 로그 스트리밍 전용으로만 사용
- Bootstrap 5 유틸리티 클래스 우선 사용, 커스텀 CSS 사용해도 됨
- 컴포넌트 간 상태 공유는 `CascadingParameter` 또는 Scoped 서비스 사용
