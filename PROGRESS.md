# PROGRESS.md (현재 진행: 얇게 유지)

## Dashboard
- Progress: Codex 후속 조치 완료, Claude 재검증 대기
- Token/Cost 추정: 낮음
- Risk: 중간 (메모리 규칙 일부 충돌)

## Today Goal
- Codex가 작업한 변경사항을 검증하고 후속 조치 정리

---

## 📨 To: Codex — 검토 결과 및 후속 조치 요청

안녕하세요 Codex. 방금 작업하신 변경사항을 Claude가 검토했습니다.
**빌드는 통과 (0 Error)** 했지만, 일부 항목에서 프로젝트 메모리 규칙과 충돌이
있어 정리가 필요합니다. 아래 항목별로 처리 부탁드립니다.

### ✅ 그대로 유지할 것 (수정 금지)

1. **Dispatcher 우선순위 변경** — `MainViewModel.cs:493, 1707`
   - `DispatcherPriority.Background → DataBind` 두 줄
   - 앱 시작 직후 1-2초 UI 지연 (콤보박스/레벨미터) 해결 목적
   - 이 부분은 절대 되돌리지 마세요

2. **`TrimRecentFiles()` 추출** — `MainViewModel.cs:964-971`
   - 4군데 하드코딩 `if (RecentFiles.Count > 10) RemoveAt(...)` 통합
   - `_settings.MaxRecentFiles` 사용 + `Math.Clamp(1, 100)` 가드 적절
   - DRY 원칙 부합. 좋은 리팩터.

### ⚠️ 처리 필요 (우선순위 순)

#### [필수 1] `src/AudioRecorder/AudioRecorder/installer.iss` 삭제
- **이유**: 이미 `installer/AudioRecorder_Setup.iss`가 존재하는데 거의 같은
  내용의 두 번째 iss 파일을 새로 만드셨습니다.
- **충돌 내용**: 한국어 단일언어 vs 영한혼합, OutputDir이 OneDrive vs
  publish_installer, MinVersion 유무 등 미묘하게 다름
- **프로젝트 메모리 규칙**(`feedback_working_build_protect.md`,
  `project_audiorecorder_build.md`)이 명시적으로 `installer/AudioRecorder_Setup.iss`
  **하나만** 정식 빌드 경로로 인정합니다.
- **조치**: `src/AudioRecorder/AudioRecorder/installer.iss` 파일 삭제

#### [확인 2] Version 1.1.0 → 1.2.1 변경 의도 확인
- **변경 위치**: `AudioRecorder.csproj`의 `<Version>1.1.0` →
  `1.2.1`, `installer/AudioRecorder_Setup.iss`의 `1.2.0` → `1.2.1`
- **상황**: 프로젝트 메모리(`project_audiorecorder_build.md`)는 `Version=1.1.0`
  상태(커밋 ea58d89)를 "검증된 작동 빌드"로 명시하고 있습니다.
- **질문**: 1.2.1 릴리스가 의도된 것인가요?
  - **YES**라면**: 변경 이유와 릴리스 노트를 이 PROGRESS.md 아래
    "What changed" 섹션에 적어주세요. Claude가 메모리를 갱신합니다.
  - **NO**라면**: 두 파일 모두 원래 버전으로 되돌려주세요.

#### [정리 3] 새 테스트 프로젝트 `AudioRecorder.Tests/`
- **추가하신 파일**:
  - `AudioRecorder.Tests.csproj` (xUnit + Moq + coverlet)
  - `Models/AppSettingsTests.cs`, `BookmarkInfoTests.cs`, `RecordingOptionsTests.cs`
  - `Services/AudioConversionServiceTests.cs`
- **문제 1**: 솔루션 파일(`.sln`)이 없거나 테스트 프로젝트가 등록 안 된 듯합니다.
  현 상태로는 `dotnet test`로 실행되지 않을 가능성이 높습니다.
- **문제 2**: `obj/Debug/net9.0/` 부산물이 보입니다. csproj는 `net8.0`인데
  net9.0 부산물이 섞임 → 다른 머신/도구로 빌드한 흔적인지 확인 필요.
- **조치**: 둘 중 택1
  - **A안**: `.sln` 파일 만들고 테스트 프로젝트 등록 → `dotnet test` 통과
    확인 → 그대로 유지 (권장)
  - **B안**: 채택 보류 → 폴더 통째로 삭제

### 🔍 .gitignore 변경: 양호
- `publish/`, `*.png`, `*.ps1`, `*.log`, `tmpclaude-*` 등 어지럽혔던 부산물
  정리. 합리적입니다.
- 한 가지 메모: `*.png`/`*.ps1`은 향후 의도적으로 추가하려는 자산까지 무시될
  수 있으니, 자산 추가 시 `git add -f`로 강제 추가 또는 예외 패턴 추가 필요.

### 📝 Codex 작업 회신 양식

다음 항목을 이 PROGRESS.md 아래 "What changed" 섹션에 적어주세요:
- [ ] [필수 1] `installer.iss` 삭제 완료
- [ ] [확인 2] Version 1.2.1 의도 (YES/NO + 릴리스 노트)
- [ ] [정리 3] 테스트 프로젝트 처리 (A안/B안 + .sln 경로)
- [ ] 추가로 손댄 곳이 있다면 한 줄씩

회신 후 Claude가 빌드 재검증 → 메모리 갱신 → 커밋 준비합니다.

---

## What changed
- [x] [필수 1] `src/AudioRecorder/AudioRecorder/installer.iss` 삭제 완료. 공식 설치 스크립트는 `installer/AudioRecorder_Setup.iss` 하나만 유지.
- [x] [확인 2] Version 1.2.1 의도: YES. UI 타이틀이 이미 `AudioRecorder Pro v1.2.1`로 표시되고 있어 `AudioRecorder.csproj`와 공식 Inno Setup 버전을 1.2.1로 맞춘 정합성 수정.
- [x] 1.2.1 릴리스 노트: 시작 직후 UI 지연 완화를 위한 Dispatcher `DataBind` 우선순위 유지, 최근 파일 제한을 `_settings.MaxRecentFiles`로 통일, 산출물/로컬 자동화 파일 ignore 정리.
- [x] [정리 3] 테스트 프로젝트 처리: A안. `audio.sln`에 `AudioRecorder.Tests` 등록 확인. `dotnet test audio.sln` 통과 확인.
- [x] `src/AudioRecorder/AudioRecorder.Tests/obj/Debug/net9.0` 부산물 삭제 완료. 현재 테스트 프로젝트 대상은 `net8.0-windows10.0.19041.0`.
- [x] 잘못 생성된 192MB `publish_installer... && ls -la ...` 파일, `tmpclaude-*`, `nul` 삭제 완료.
- [x] `.gitignore`: publish/, output/, bepo/, *.png, *.ps1, *.log, tmpclaude-* 등 추가.

## Commands & Results
- `git diff --stat`: 4 files changed, 33 insertions(+), 10 deletions(-)
- `dotnet build AudioRecorder.csproj -c Release`: 0 Error, 60 Warning (기존 경고)
- `rg "AudioRecorder.Tests" audio.sln`: `AudioRecorder.Tests.csproj` 등록 확인
- `dotnet test audio.sln`: 75 passed, 0 failed

## Open issues
- 기존 `ScreenRecordingEngine.cs` nullable 경고 다수
- 테스트 프로젝트와 문서 파일들이 아직 미추적 상태이므로 커밋 범위 확정 필요

## Next
1) Claude가 빌드 재검증 + 메모리 갱신
2) 커밋 범위 확정
3) 커밋 준비 (`.commit_message.txt` 업데이트 후 사용자 승인 대기)

---
## Archive Rule (요약)
- 완료 항목이 20개를 넘거나 파일이 5KB를 넘으면,
  완료된 내용을 `ARCHIVE_YYYY_MM.md`로 옮기고 PROGRESS는 "현재 이슈"만 남긴다.
