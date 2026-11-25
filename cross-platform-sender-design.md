# Network HID Bridge 송신기 이식 설계

## 목표와 기준 소스

Windows x64와 Linux amd64 데스크톱에서 물리 키보드와 상대좌표 마우스를 기존 Raspberry Pi 수신기로 보낸다. 송신기 언어는 C#으로 유지한다. Windows에는 기존 WPF/WinForms 앱을 제공하고 Linux에는 Avalonia 앱을 제공한다. Pi는 TCP로 받은 입력을 USB HID로 출력하는 기존 Python 수신기를 사용한다.

구현 시작점은 [검토한 커밋](https://github.com/Rakctite/raspi_tcp_usb_device/tree/4a9201f13edb83ab0d8b074c6b20ab086573d679)이다. 아래 경로는 이 저장소 루트 기준이다. 빈 작업 디렉터리에서 시작한다면 이 소스를 먼저 체크아웃한다. 더 최신 커밋을 사용할 때는 입력 모드, 프로토콜 및 테스트 경로의 변경 사항을 먼저 대조한다.

## 프로젝트 경계

| 대상 | 구현 지침 |
| --- | --- |
| NetworkHidBridge.Core | 기존 프로젝트는 이미 net9.0이다. Protocol, Networking, ModeController, KeyboardReportState, MouseAccumulator와 관련 테스트를 공유한다. 프로젝트 경로를 유지해도 된다. 코드 공유를 위해 솔루션 전체를 재배치할 필요는 없다. |
| Windows 앱 | 기존 UI, 트레이, 훅, Raw Input, 가상 키 매핑, 자동 시작과 DPAPI 설정 저장을 유지한다. Core의 ProtectedSettingsStore와 StartupTaskManager 및 전용 테스트는 Windows 프로젝트 또는 Windows 전용 라이브러리로 이동한다. ProtectedData 패키지도 함께 이동한다. 기존 설정 경로·형식·사용자 키를 읽는 회귀 테스트를 유지한다. |
| Linux 앱 | src/linux/NetworkHidBridge.Linux에 net9.0 Avalonia 프로젝트를 추가한다. 장치 열기·읽기·독점·닫기는 별도 Linux 입력 클래스로 격리한다. Linux 프로젝트가 참조하는 솔루션 하위 집합만으로 Linux에서 복원·빌드할 수 있게 한다. |
| Raspberry Pi | docs/protocol-v1.md, tests/fixtures/protocol-v1.json 및 기존 수신기를 호환성 기준으로 삼는다. FrameCodec의 기존 공개 API와 바이트 인코딩을 보존한다. |

양쪽 UI는 BridgeClient의 연결 상태와 ModeController의 모드를 표시한다. 입력 콜백은 TCP에 직접 쓰지 않고 기존 QueueKeyboard, QueueConsumer, AddMouseMovement, AddMouseWheel, SetMouseButton을 호출한다. 재연결, heartbeat, 마우스 병합 및 FullState 스케줄러는 BridgeClient 한 곳에서 실행한다. 키보드 상태와 소비자 키는 변경 시, 마우스는 최대 125Hz, 원격 모드의 전체 상태는 500ms 주기로 전송한다. 세션 종료와 시간 제한의 정확한 동작은 프로토콜 v1을 따른다.

## Linux 입력과 모드 전환

첫 버전의 선택 단위는 물리 키보드 하나와 상대좌표 마우스 하나다. 복합 USB 장치는 선택한 기능에 해당하는 여러 event 노드를 가질 수 있다. 장치 목록에는 이름과 구분 가능한 물리 경로를 표시하고 안정적인 ID를 저장한다. 재실행 시 event 번호만으로 다른 장치에 자동 연결하지 않는다. 터치패드의 절대좌표·제스처는 첫 버전 범위 밖이다.

1. evdev 파일을 비동기로 감시한다. C#에서 커널 ABI나 libevdev를 얇게 호출하고 핸들은 SafeHandle 또는 동등한 수명 관리로 닫는다. 읽기 루프와 UI 스레드를 분리한다. 장치 접근은 선택한 장치에 대한 사용자 세션 ACL 또는 좁은 udev 규칙으로 제공한다. 권한 오류에는 실패한 장치와 조치 방법을 표시한다.
2. EV_KEY 값 1/0을 누름/뗌으로 처리하고 값 2는 중복 누름으로 보내지 않는다. Linux KEY_*를 물리 USB HID usage로 매핑하고 좌우 modifier, 숫자패드, 소비자 키를 구분한다. Windows KeyboardMapper의 가상 키 번호를 Linux 코드로 재해석하지 않는다. EV_REL의 X/Y와 수직·수평 휠, 버튼 1~5를 기존 마우스 표현으로 변환한다. 고해상도 휠과 일반 휠이 함께 보고될 때 중복 계산하지 않는다. SYN_DROPPED 시 상태 추정을 중단하고 Default로 복구한다.
3. Default는 로컬만, Shared는 양쪽, RemoteOnly는 원격만, BlockAll은 양쪽 차단이다. 허용 여부는 기존 ModeController 정책을 따른다. RemoteOnly와 BlockAll에서는 선택한 입력 노드 모두에 EVIOCGRAB을 획득한다. 하나라도 실패하면 이미 얻은 grab을 전부 해제하고 Default로 돌아간다. 화면에는 실제 적용에 성공한 모드만 표시한다.
4. 숫자패드 빼기와 곱하기 조합은 기존처럼 Default와 RemoteOnly를 전환한다. 자동 반복으로 재전환하지 않는다. 일반 evdev 관찰 중에는 앞서 로컬에 전달된 키를 취소할 수 없다. 첫 Linux 버전에서는 전환을 예약한 뒤 단축키를 포함해 누른 키·버튼이 모두 해제된 이벤트 경계에서 grab을 바꾼다. 대기 중에는 기존 라우팅을 유지하고 전환 조합은 원격 보고서에서 제외한다. 따라서 Default에서 조합이 로컬 앱에 보일 수 있음을 UI 도움말과 테스트에 명시한다. UI 요청도 동일한 전환 경로를 사용한다.
5. 인증 단절, 장치 분리, 세션 잠금/비활성화, 종료 시에는 대기 중인 전환을 취소하고 즉시 grab을 풀어 Default로 복귀한다. 살아 있는 세션에서는 기존 해제 경로로 ReleaseAll을 보내며 네트워크 응답을 기다리느라 로컬 복구를 지연하지 않는다. 연결이 이미 끊긴 경우 Pi의 세션 정리 로직으로 HID 해제를 검증한다. 재연결만으로 원격 모드에 재진입하지 않는다. 모드 변경·장치 변경·연결 종료를 한 곳에서 직렬화하고 이전 세션의 이벤트나 비동기 완료가 새 모드를 덮어쓰지 않게 한다.

## Linux UI와 설정

연결 주소·포트, PSK 입력, 키보드·마우스 선택, 연결 및 인증 상태, 현재 모드, 종료를 한 창에 제공한다. 기본 주소와 포트는 기존 AppSettings 값을 사용한다. 원격 활성화 전 인증 상태와 장치 사용 가능 여부를 확인한다. 원격 모드에서 창에 접근할 수 없어도 단축키로 복귀할 수 있어야 한다. 초기 Linux 버전에서 창 닫기는 전체 종료로 정의한다. Windows의 트레이 종료 및 자동 시작 동작은 기존대로 유지한다.

일반 설정은 XDG_CONFIG_HOME 아래 사용자별 JSON에 저장한다. 미설정 시 ~/.config를 사용한다. PSK는 사용자 세션의 Secret Service에 별도로 저장한다. 키링이 잠겼거나 없으면 세션 한정 입력을 허용하고 저장 상태를 표시한다. Base64 입력은 정확히 32바이트로 디코딩되는지 검사한다. 키는 로그·예외 메시지에 넣지 않는다. 잘못된 설정은 사용자에게 표시하고 수정할 수 있게 한다. 설정 파일은 같은 디렉터리의 임시 파일을 완성한 후 원자적으로 교체한다.

## 구현 순서와 인수 기준

| 단계 | 실행 및 통과 조건 |
| --- | --- |
| 1. 기존 동작 고정 | Windows에서 기존 솔루션 빌드 및 dotnet test, Python에서 python -m pytest tests/pi -q를 실행해 기준 결과를 기록한다. Core 분리 후 Windows 설정 로드, 단축키, 트레이 종료가 동일하게 동작해야 한다. |
| 2. Linux에서 공통 통신 확인 | Linux에서 공통 테스트와 PythonInteropTests의 이식 가능한 케이스를 실행한다. tests/pi/fake_hid_server.py를 사용하는 기존 테스트 흐름으로 정상 PSK, 잘못된 PSK, 연결 단절과 재인증을 확인한다. 양쪽에서 같은 protocol-v1.json을 읽는다. |
| 3. Linux 입력과 전환 | 합성 이벤트로 좌우 Shift, 숫자패드 조합과 반복, 동시 키, 소비자 키 해제, 마우스 버튼·휠·pan, 일부 grab 실패, 전환 도중 연결 단절을 검사한다. 테스트에서 입력 파일과 grab 결과를 대체할 수 있게 한다. |
| 4. UI 및 실기기 | Linux amd64 데스크톱에서 일반 사용자로 실행한다. 로컬 편집기와 Pi 대상 편집기를 함께 열어 네 모드별 전달 여부를 관찰한다. 누른 채 모드 전환, 잘못된 권한, 키링 부재, 장치 탈착, TCP 케이블 분리, 프로세스 강제 종료 후 로컬 복귀와 대상 키·버튼 해제를 확인한다. 사용한 배포판, 세션 종류, 장치와 결과를 기록한다. |
| 5. 배포 | 각 OS에서 해당 UI 프로젝트를 dotnet publish -c Release -r win-x64 또는 linux-x64 --self-contained true로 게시한다. Linux 패키지에는 실제 사용한 Avalonia·evdev·키링 네이티브 의존성과 장치 권한 설정법을 명시한다. SDK 없는 대상 환경에서 실행·설정 저장·연결·복귀까지 확인한다. |

자동 테스트 결과와 실기기 결과를 구분해 인계한다. 특히 가짜 수신기로 확인한 TCP 호환성과 실제 데스크톱에서의 독점 입력·USB HID 출력은 각각 별도 인수 항목이다.
