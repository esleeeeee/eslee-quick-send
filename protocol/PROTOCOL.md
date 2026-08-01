# QuickSend Wire Protocol 1

## 전송 계층

모든 애플리케이션 프레임은 TLS 1.3(불가능한 플랫폼에서는 TLS 1.2 이상) TCP 연결 안에서만 교환합니다. 설치마다 P-256 TLS identity를 만들고 private key는 Windows 인증서 저장소 또는 Android Keystore에 둡니다. 재연결할 때 상대 인증서의 SubjectPublicKeyInfo SHA-256 지문이 신뢰 DB와 일치해야 합니다.

최초 페어링 연결은 암호화되어 있지만 아직 신뢰되지 않은 인증서를 허용합니다. 두 공개키 지문과 nonce를 정렬해 SHA-256한 값으로 6자리 SAS를 만들고 양쪽 사용자가 같은 번호를 승인한 뒤에만 지문을 저장합니다. 미페어링 연결은 페어링 프레임 외의 요청을 실행할 수 없습니다.

## LAN discovery 계약

QuickSend v1의 DNS-SD 계약은 다음 값이 기준이다.

| 항목 | 값 |
|---|---|
| canonical service type | `_eslee-quicksend._tcp` |
| wire DNS-SD name | `_eslee-quicksend._tcp.local.` |
| Windows Makaretu API 입력 | `_eslee-quicksend._tcp` |
| Android `NsdManager` API 입력 | `_eslee-quicksend._tcp.` |
| domain | `local.` |
| protocol | TCP (`NsdManager.PROTOCOL_DNS_SD`) |
| port | `41231` |
| instance name | 영숫자 device ID |
| TXT `id` | device ID |
| TXT `name` | 사용자 표시용 기기 이름 |
| TXT `fp` | P-256 identity 공개키 SHA-256 지문(대문자 hex 64자) |
| TXT `v` | wire protocol version (`1`) |

Windows와 Android API 입력의 trailing dot 차이는 각 라이브러리의 요구 형식에 따른 의도적인 차이다. `.local.`은 Windows `ServiceProfile`과 Android NSD가 DNS-SD 레코드를 만들 때 붙이는 domain이므로 Android `discoverServices()` 입력에 직접 포함하지 않는다. capability 목록은 discovery TXT가 아니라 TLS 연결 후 `HELLO`에서 교환한다.

### 수동 endpoint bootstrap

수동 IP 연결은 DNS-SD가 제공하던 IP와 port를 사용자가 직접 입력하는 bootstrap 경로일 뿐 별도 wire protocol이 아니다. Android client는 입력 endpoint에 TCP 연결한 후 기존 TLS mutual-auth, `HELLO`, 인증서 지문 검증, 필요 시 `PAIR_REQUEST`/`PAIR_ACCEPT` SAS 절차를 그대로 수행한다. handshake 확인에는 기존 `PING`/`PONG`을 사용하며, 성공한 `deviceId`/이름/지문/IP/port는 자동 발견 peer와 같은 모델에 등록한다. 이후 파일 전송은 기존 `JOB_MANIFEST` 이하의 전송 경로를 사용한다.

## 프레임 헤더

모든 정수는 network byte order(big endian)입니다. offset과 길이 누계는 unsigned 64-bit입니다.

```text
offset  size  field
0       4     magic = ASCII "ESQ1"
4       2     headerSize = 32
6       2     protocolVersion = 1
8       2     messageType
10      2     flags
12      8     monotonically increasing sequence
20      8     payloadLength
28      4     reserved = 0
```

헤더는 정확히 32바이트입니다. control payload 최대 크기는 4 MiB, chunk payload 최대 크기는 협상한 청크 크기 + 60바이트이고 절대 상한은 64 MiB + 60바이트입니다. 한계를 넘는 프레임은 payload를 할당하기 전에 연결을 종료합니다.

## 메시지 형식

control payload는 UTF-8 JSON입니다. 알 수 없는 JSON 필드는 무시하되 필수 필드가 없거나 타입이 틀리면 요청을 거부합니다.

| 값 | 이름 | 용도 |
|---:|---|---|
| 1 | `HELLO` | 버전, 기능, identity, device 정보 |
| 2-3 | `PAIR_REQUEST`, `PAIR_ACCEPT` | SAS 페어링 |
| 4 | `JOB_MANIFEST` | 파일/폴더 목록과 64-bit 크기 |
| 5 | `FILE_START` | 파일 metadata와 chunk 설정 |
| 6 | `RESUME_INFO` | 수신측 durable offset/root state |
| 7 | `CHUNK_DATA` | 바이너리 청크 |
| 8 | `CHUNK_ACK` | 검증·기록된 청크와 수신 offset |
| 9 | `CHECKPOINT` | durable committed offset |
| 10-12 | `FILE_COMPLETE`, `FILE_VERIFY`, `JOB_COMPLETE` | 종료/검증 |
| 13-15 | `PAUSE`, `RESUME`, `CANCEL` | 사용자 제어 |
| 16-17 | `PING`, `PONG` | watchdog |
| 18 | `ERROR` | 안정적인 오류 code와 복구 등급 |

`CHUNK_DATA` payload만 JSON이 아닙니다.

```text
16 bytes fileId (RFC 4122 network order)
8  bytes offset
4  bytes dataLength
32 bytes SHA-256(data)
N  bytes data
```

## 재개와 체크포인트

수신측은 청크 해시를 확인하고 정해진 offset에 기록합니다. 중복 청크가 이미 committed offset 아래라면 다시 쓰지 않고 idempotent ACK를 반환합니다. gap, 겹침, 다른 해시의 중복은 오류입니다.

수신측은 64 MiB 또는 1초마다 다음 순서로 처리합니다.

1. 부분 파일 데이터와 metadata를 durable flush
2. DB transaction으로 `committedOffset`과 Merkle accumulator snapshot 저장
3. transaction commit
4. `CHECKPOINT` 전송

같은 청크에 `CHECKPOINT`와 `CHUNK_ACK`가 모두 발생하면 수신측은 반드시 `CHECKPOINT`를 먼저 전송합니다. 마지막 `CHUNK_ACK`는 송신측 ACK 루프를 종료시킬 수 있으므로 이 순서를 지켜야 체크포인트가 다음 `FILE_VERIFY` 응답으로 잘못 해석되지 않습니다.

재연결 시 송신 기록과 무관하게 수신 `RESUME_INFO.committedOffset`에서 시작합니다. 실제 부분 파일 길이가 DB보다 짧으면 안전한 이전 청크 경계로 rollback합니다. 길이가 더 길면 DB offset으로 truncate합니다.

수신 파일이 이미 최종 이름으로 완료된 뒤 연결이 끊긴 경우에는 최종 파일을 다시 열거나 새 부분 파일을 만들지 않습니다. 저장된 Merkle snapshot과 최종 파일 존재 여부를 확인하고 `committedOffset=size`를 응답한 뒤, 재전송된 `FILE_COMPLETE`를 검증하여 `FILE_VERIFY`를 재응답합니다.

## 흐름 제어

기본값은 8 MiB 청크, 8 청크(64 MiB) in-flight window입니다. 송신 루프와 ACK 수신 루프를 분리하고 semaphore로 미확인 바이트 수를 제한합니다. ACK는 청크를 메모리에서 해제하며 `CHECKPOINT`만 사용자에게 안전 완료 진행률로 반영합니다.

청크 해시 실패 `ERROR`는 `fileId`와 `offset`을 포함합니다. 송신측은 보관 중인 in-flight buffer에서 해당 청크만 재전송합니다. 동일 청크가 세 번째 실패하면 양쪽이 연결을 닫고 마지막 durable checkpoint에서 새 연결을 만든다.

송신측은 활성 전송 중 20초마다 `PING`을 보내며 45초 이상 어떤 응답도 없으면 연결을 stalled로 판정한다. `PING`과 `PONG`은 control 응답을 기다리는 모든 상태에서 처리할 수 있다.

## 최종 검증

각 청크 SHA-256을 leaf로 사용하고 pairwise SHA-256(`0x01 || left || right`)으로 Merkle root를 계산합니다. 홀수 노드는 자기 자신과 결합합니다. leaf는 `SHA-256(0x00 || data)`입니다. 전체 길이, leaf 수, root가 일치하고 durable flush가 끝나야 부분 파일을 충돌 없는 최종 이름으로 원자 확정합니다.
