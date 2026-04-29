# MEMORY.md (SSOT: 규칙/기술 스택/제약)

## 1) Goal / Scope (정적)
- 목표:
- 범위:
- Non-goals:

## 2) Tech Stack (정적, 캐시 최적화)
- Framework:
- Language:
- State/Networking:
- Backend/DB:
- Build/CI:
- Target platforms:

## 3) Constraints (가끔 변함)
- OS/Node/Java/Gradle/SDK 버전:
- 빌드/배포 제약:
- 성능/번들 제약:
- 금지사항(예: Expo 금지/허용 등):

## 4) Coding Rules (정적)
- 최소 diff 원칙
- 테스트/수정 루프(최대 3회): lint/typecheck/test 우선
- 비밀정보 금지: 값 금지(변수명/위치만)
- 큰 변경(프레임워크/DB/상태관리 교체)은 사용자 1회 확인 후 진행

## 5) Architecture Notes (가끔 변함)
- 폴더 구조 요약:
- 주요 모듈 책임:
- 데이터 흐름:

## 6) Testing / Release Rules (정적)
- 통과 기준(lint/typecheck/test):
- 릴리즈 체크리스트 위치:
