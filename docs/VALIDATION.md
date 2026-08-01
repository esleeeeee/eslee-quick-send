# 검증 기록 — 2026-07-14

검증 호스트: Windows x64, .NET SDK 10.0.301, Temurin JDK 21.0.11, Android SDK 36 / Build Tools 36.0.0, Gradle 9.4.1, AGP 9.2.0.

| 테스트명 | 환경 | 파일 크기 | 실행 여부 | 결과 | 속도 | 재시도 | 비고 |
|---|---|---:|---|---|---|---:|---|
| .NET core 자동 테스트 | host process | 메모리 payload 0–130,001 B 및 20 B fault fixture | 실행 | 14/14 통과 | 미측정 | 해당 없음 | DNS-SD contract 및 resume/rollback/corruption 포함 |
| Windows Debug build | .NET 10 + WinUI 3 | 해당 없음 | 실행 | 경고 0, 오류 0 | 해당 없음 | 0 | self-contained x64 |
| Windows TCP listener | 실제 Windows 호스트 | TCP 41231 | 실행 | `192.168.123.102:41231` 연결 성공 | 해당 없음 | 0 | `[::]:41231`, IPv4/IPv6 dual-mode |
| Windows listener 진단 이벤트 | 실제 Windows 앱 로그 | bound address/port/dual-mode/accept | 실행 | 요청 이벤트 모두 기록 확인 | 해당 없음 | 0 | LAN IPv4 `192.168.123.102`, accept remote/local endpoint 확인 |
| Windows mDNS 광고 | 실제 Realtek Ethernet (`192.168.123.102/24`) | 별도 `QuickSend.MdnsProbe` 프로세스의 DNS-SD PTR/SRV/TXT/A 질의 | 실행 | `_eslee-quicksend._tcp.local.` 응답 확인 | 해당 없음 | 0 | SRV port 41231, A=192.168.123.102, TXT id/name/fp/v 확인 |
| Windows 방화벽/네트워크 | Public Ethernet | Defender Firewall 및 규칙 감사 | 실행 | Defender 3개 프로필 모두 비활성, 기본 mDNS 규칙 활성 | 해당 없음 | 해당 없음 | QuickSend 전용 inbound 규칙은 없음 |
| Windows UI/tray lifecycle | 실제 Windows 셸 + UI Automation/Win32 입력 | 해당 없음 | Tests 1–7 실행 | 모두 통과 | 미측정 | 0 | 실제 창/HWND/가시성, 트레이 더블클릭·메뉴 열기·종료, 최소화 복원, 전면화, 재실행, 아이콘 제거 확인 |
| Windows single-instance | 소스 감사 | 해당 없음 | 확인 | 해당 없음 | 해당 없음 | 해당 없음 | single-instance 구현이 없어 Test 8은 적용 대상 아님 |
| Android JVM unit test | JDK 21, 영문 junction | 작은 메모리 fixture | 실행 | 8/8 통과 | 미측정 | 해당 없음 | DNS-SD contract와 manual IPv4/port 유효성 포함, XML failures=0/errors=0 |
| Android manual endpoint contract | 소스/컴파일 감사 | TCP→TLS→HELLO→SAS→PING/PONG | 실행 | 기존 handshake와 peer/queue 경로 재사용 확인 | 해당 없음 | 해당 없음 | 성공 peer를 같은 `DiscoveredDevice` 목록에 등록, discovery 계속 유지 |
| Android NSD lifecycle 정적/빌드 검증 | targetSdk 36, `NsdManager` | register/discover/resolve/lost/stop | 실행 | 컴파일·lint 통과 | 해당 없음 | 해당 없음 | multicast lock 선획득, 불필요한 nearby 권한 시작 게이트 제거, 상세 callback 로그 추가 |
| Android startup 정적 점검 | Application→Activity→Compose→FGS→DB/Keystore/NSD | 해당 없음 | 실행 | 고확률 예외 전파 지점 격리 | 해당 없음 | 해당 없음 | UI 선표시, 단계별 실패 로그, 서비스 실패 시 UI 유지 |
| Android Debug APK build | SDK 36, 캐시 비활성 clean build | 18,698,116 B | 실행 | 성공 | 해당 없음 | 0 | manual IP dialog 포함 debug APK 생성, v2 서명 검증 |
| Android lintDebug | Android lint | 해당 없음 | 실행 | 오류 0, 경고 16 | 해당 없음 | 해당 없음 | startup 관련 InlinedApi/Wakelock 경고 해소 |
| Android merged/APK manifest | aapt2 + merged manifest | 해당 없음 | 실행 | package/SDK/activity/service/FGS type/permissions 일치 | 해당 없음 | 해당 없음 | service exported=false, connectedDevice=0x10 |
| Android emulator cold start | Emulator/AVD 필요 | 해당 없음 | 미수행 | 실행 가능한 emulator/AVD 없음 | 미측정 | 미측정 | 대규모 SDK 설치 생략 |
| PC↔Android 실제 전송 | 실기기 필요 | 미정 | 미수행 | 실제 기기 검증 필요 | 미측정 | 미측정 | 기기 미연결 |
| 화면 OFF/백그라운드 | 실기기 필요 | 미정 | 미수행 | 실제 기기 검증 필요 | 미측정 | 미측정 | 정책 우회 없음 |
| 10/50/100 GiB | 실기기 필요 | 미정 | 미수행 | 미수행 | 미측정 | 미측정 | 허위 통과 주장하지 않음 |
| Quick Share 성능 비교 | 동일 기기·AP 필요 | 미정 | 미수행 | 미수행 | 미측정 | 해당 없음 | 목표 달성 여부 미판정 |

생성 산출물:

- Android debug APK: 18,698,116 bytes, SHA-256 `33D40E6A51DDE8A52D7A4B229D21855B13C6684616C68F5746831B221D8A923F`
- Windows debug launcher: 291,328 bytes, SHA-256 `990FC1D4200FC02E645963489D58083946B4D1697CBA1A11A01E0F6C5B5E294A`
- Windows application DLL: 400,896 bytes, SHA-256 `C9C403EF92139A537F5135F2DF9C629FC3067C1CDA5C6100D4E83B7F1076EA1F` (수정된 코드는 이 DLL에 포함되며, EXE는 같은 출력 폴더의 DLL/WinUI 리소스와 함께 실행해야 함)

## 알려진 제한

- Android를 사용자가 강제 종료(force-stop)하면 운영체제 정책상 자동으로 되살릴 수 없다. 다음 명시적 실행 후 복구한다.
- Windows 수신 경로는 현재 `Downloads\eslee QuickSend`로 고정되어 있고 Android는 SAF로 변경 가능하다.
- 원격 취소 시 상대 partial 삭제 선택을 wire로 확정하는 세부 정책은 후속 보강 대상이다.
- 실제 Wi-Fi 품질, OEM 백그라운드 정책, 방화벽, mDNS 격리망에서의 동작은 실기기 검증이 필요하다.
- Android 실기기가 연결되지 않아 Windows→Android 및 Android→Windows 실제 NSD 발견은 아직 검증하지 못했다.
- PC→phone ping은 사용자가 `192.168.123.102`→`192.168.123.100` 4/4, 손실 0%로 확인했지만 새 APK의 manual TLS/SAS/100 MB 전송은 실기기에서 아직 수행하지 않았다.
- 자동 장애 테스트는 protocol/storage 경계 중심이다. 실제 socket proxy 기반 chaos와 장시간 soak test는 아직 없다.
- v1은 같은 LAN TCP만 지원하며 Wi-Fi Direct, 인터넷 relay, iOS는 없다.
