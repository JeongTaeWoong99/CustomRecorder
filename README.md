# CustomRecorder

광고 없는 Windows 화면 녹화 프로그램 (C# / .NET 8 / WPF).
자주 쓰는 녹화 기능을 최신 Windows API(Windows.Graphics.Capture, Media Foundation)로 구현했습니다.

## 설치

1. [Releases](https://github.com/JeongTaeWoong99/CustomRecorder/releases)에서 최신 `CustomRecorder-vX.Y.Z-win-x64.zip`을 받습니다.
2. 압축을 풀고 원하는 폴더에 `CustomRecorder.exe`를 둔 뒤 실행합니다. .NET을 따로 설치할 필요가 없습니다.
   - 처음 실행할 때 Windows SmartScreen이 경고하면 "추가 정보 → 실행"을 누르세요 (서명하지 않은 프로그램이라 뜨는 경고).
3. Windows 10 2004(19041) 이상, 64비트.

## 업데이트

- 프로그램을 켤 때 새 버전이 있으면 툴바에 **업데이트** 버튼이 나타납니다.
- 직접 확인: 설정 → **정보 · 업데이트** → 업데이트 확인.
- 업데이트는 받은 파일의 SHA-256을 확인한 뒤 실행 파일을 교체하고 다시 시작합니다. 설정(`%AppData%\CustomRecorder`)은 그대로 남습니다.
- 녹화·리플레이 중에는 업데이트하지 않습니다.

## 기능

### 기본 녹화

| 기능 | 내용 |
| --- | --- |
| 녹화 대상 | 전체 화면(모니터 선택), 드래그로 지정한 영역, 특정 창(가려져도 녹화) |
| 영상 | H.264 High, GPU 인코더 자동 선택(NVENC / Quick Sync / AMF), 30·60fps 고정 프레임 |
| 파일 | 조각 저장 MP4 — 녹화 중 강제 종료돼도 그 시점까지 재생 가능 |
| 소리 | 시스템 소리(WASAPI 루프백) + 마이크, 48kHz AAC, 실시간 레벨 표시 |
| 기타 | 일시정지, 스크린샷(PNG/JPG + 클립보드), 전역 단축키, 툴바 자동 녹화 제외 |

단축키(설정에서 변경 가능): `F2` 녹화/중지 · `Shift+F2` 일시정지 · `F3` 캡처 · `F4` 리플레이 저장

### 리플레이 · 출력 · 효과

| 기능 | 내용 |
| --- | --- |
| 리플레이 버퍼 | 켜 두면 최근 30초~5분을 기억, 단축키로 저장 |
| 출력 | H.264 / HEVC, 비트레이트, 키프레임 간격, 저장 해상도(GPU 축소) |
| 효과 | 커서 강조, 클릭 물결, 워터마크 (Direct2D로 프레임에 직접 그림) |
| 편의 | 소리만 녹음(M4A/MP3/FLAC/WAV), 트랙 분리, 카운트다운, 자동 종료, 녹화 후 동작, 트레이, 파일 이름 규칙 |

### 편의 기능

| 기능 | 내용 |
| --- | --- |
| 창 선택 | Alt+Tab처럼 실제 창 화면을 미리보기 카드로 표시 (DWM 썸네일), 검색 |
| 디자인 | 다크 테마, Windows 11 둥근 창·그림자 |
| 키 입력 표시 | 누른 단축키(또는 모든 키)를 영상 아래에 말풍선으로 표시 |
| 웹캠 PIP | 웹캠 화면을 원·둥근 사각형으로 영상 구석에 합성 |
| 최근 녹화 | 썸네일 목록, 재생·탐색기·휴지통 |
| 자르기 | 미리보기에서 구간을 골라 재압축 없이 저장 (키프레임 기준) |
| 예약 녹화 | 정한 시각에 시작해 정한 길이만큼 녹화 |
| 상태 표시 | 녹화 시간 표시, 저장 드라이브 여유 공간 |

### 그리기 · GIF · 작업 표시줄

| 기능 | 내용 |
| --- | --- |
| 화면에 그리기 | 펜·형광펜·지우개, 6색, 굵기 3단계, 되돌리기. 그림은 녹화에 찍히고 도구 막대는 안 찍힘 |
| GIF 저장 | 자르기 창에서 고른 구간을 GIF로 저장. 바뀐 영역만 저장하는 자체 GIF 인코더 |
| 파일 정리 | 녹화 종료 후 조각 MP4를 일반 MP4로 변환(상자 단위 직접 변환, 재압축 없음) → 모든 플레이어에서 길이·탐색 정상 |
| 저장 공간 보호 | 남은 공간이 512MB 아래로 내려가면 녹화를 멈추고 저장 |
| 항상 위 / 단축키 끄기 | 툴바 고정 토글, 단축키별 사용 안 함 |
| 작업 표시줄 빠른 버튼 | 알림 영역 바로 왼쪽 빈자리에 녹화(녹화 중엔 경과 시간)·캡처·리플레이 버튼. 다른 작업 표시줄 위젯이 있으면 그 왼쪽에 붙음. 알림 영역 아이콘 방식도 선택 가능. 미리보기 창 버튼도 제공 |
| 마이크 잡음 줄이기 | 자동(방 소음을 재서 기준을 정함) / 직접 설정(실시간 소리 막대에서 기준을 끌어서 지정) |

## 구조

```
src/
  CustomRecorder.Core/          녹화 엔진 (UI 없음)
    Capture/   D3DContext       D3D11 장치 (캡처와 인코더가 공유)
               WgcCapture       Windows.Graphics.Capture 래퍼
               Screenshot       한 프레임 캡처 → BGRA
    Encoding/  Mp4Writer        Media Foundation Sink Writer (H.264 + AAC, fMP4)
    Audio/     AudioEngine      루프백·마이크 캡처, 48kHz 스테레오 믹싱, 무음 재생으로 루프백 유지
    Recording/ ScreenRecorder   캡처 → 최신 프레임 → 고정 프레임 비디오 펌프 / 오디오 펌프
               RecordingClock   일시정지 구간을 뺀 녹화 시간
  CustomRecorder.App/           WPF UI (툴바, 영역 선택, 창 선택, 설정)
```

### 데이터 흐름

1. WGC가 화면이 바뀔 때마다 GPU 텍스처를 넘김 → 녹화 영역만 `_latest` 텍스처에 복사 (GPU 안에서만 이동)
2. 비디오 펌프가 `1/fps` 간격으로 `_latest`를 텍스처 풀의 텍스처에 복사해 인코더에 전달 (화면이 안 바뀌면 같은 프레임 반복 → CFR).
   인코더가 다 쓴 텍스처(Sink Writer 통계로 확인)만 다시 쓰므로 매 프레임 새 텍스처를 만들지 않음
3. 오디오 펌프가 녹화 시계 기준으로 정확히 `시간 × 48000` 샘플을 믹서에서 읽어 기록 → 소리가 없는 구간도 끊김 없음
4. Sink Writer가 BGRA → NV12 변환과 H.264 인코딩을 GPU에서 처리

## 빌드

- .NET 8 SDK, Windows 10 2004(19041) 이상
- `build.bat` → 프로젝트 폴더에 `CustomRecorder.exe` (릴리스와 같은 self-contained 단일 파일)
- Rider / Visual Studio에서 `CustomRecorder.sln`을 열어도 됩니다.
- `--visible` 인수로 실행하면 앱 창이 화면 캡처에서 숨겨지지 않고, 로그가 실행 파일 옆 `logs\`에 남습니다 (개발·테스트용).
- 로그: `%LocalAppData%\CustomRecorder\logs\app.log` / 설정: `%AppData%\CustomRecorder\settings.json`

## 새 버전 내보내기

1. `src/CustomRecorder.App/CustomRecorder.App.csproj`의 `<Version>`을 올립니다 (예: `0.1.1`).
2. `CHANGELOG.md` 맨 위에 `## [0.1.1] - 날짜`와 변경 내용을 적습니다. 이 내용이 릴리스 노트와 프로그램의 업데이트 안내에 그대로 나옵니다.
3. 커밋하고 `main`에 push합니다 (GitHub Desktop: Commit → Push origin).

GitHub Actions가 그 버전의 릴리스가 아직 없으면 테스트·빌드한 뒤 태그 `v0.1.1`과 릴리스(zip + 체크섬)를 만듭니다.
몇 분 뒤 각 PC의 프로그램이 업데이트를 알려 줍니다. 버전 번호를 올리지 않은 push는 릴리스를 만들지 않습니다.

버전에 `-beta.1`처럼 꼬리를 붙이면(`0.2.0-beta.1`) 미리보기(pre-release)로 올라가고, "미리보기 버전도 받기"를 켠 PC에만 나타납니다.

## 의존성

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11, DXGI, MediaFoundation, Direct2D) — MIT
- [NAudio](https://github.com/naudio/NAudio) (WASAPI) — MIT

## 테스트

화면·GPU가 필요 없는 부분(파일 이름 규칙, 노이즈 게이트, GIF 인코더, NV12 축소 변환, 조각 MP4 변환기, 버전 비교)은 자동 테스트가 있습니다.

```
dotnet test tests/CustomRecorder.Tests -c Release -p:Platform=x64
```

조각 MP4 테스트는 Media Foundation과 같은 구조의 파일을 직접 만들어, 변환 후 모든 샘플이 바이트 단위로 같은지,
재생 시간·키프레임 표가 맞는지, 녹화 중 꺼져 끝이 잘린 파일을 살려내는지 확인합니다.

## 라이선스

[MIT](LICENSE)
