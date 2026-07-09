# Offline Screen Monitor

폐쇄망에서 원격지 Windows PC(전송측)의 화면을 관리자 PC(수신측)에서 들여다보는 최소 기능 모니터다. 설계 근거와 전체 요구사항은 [REQUIREMENTS.md](REQUIREMENTS.md)에 있다.

## 방향

**전송측이 수신측으로 접속을 건다(push).** 수신측만 고정 IP·방화벽 대상이고, 전송측은 최초 1회 설치 후 건드리지 않는다. 자세한 근거는 REQUIREMENTS.md §4~5.

```
전송측(원격, 만지지 않음)  --TCP connect-->  수신측(고정 IP, 관리자 PC)
                          --JPEG 프레임-->
                          <--PAUSE/RESUME--
```

## 구성

| 프로젝트 | 산출물 | 역할 |
| --- | --- | --- |
| `src/Monitor.Protocol` | (라이브러리) | 와이어 포맷 한 곳. 핸드셰이크, 프레임/제어 메시지 |
| `src/Monitor.Sender` | `ScreenSender.exe` | 화면 캡처 → JPEG → 전송. 트레이 아이콘만. 설정은 전부 빌드에 고정 |
| `src/Monitor.Receiver` | `ScreenReceiver.exe` | 리스닝 → 프레임 표시. 최소화 시 캡처 중단 신호 전송 |

둘 다 self-contained 단일 exe로 퍼블리시되므로 대상 PC에 .NET 설치가 필요 없다.

## 빌드 (개발 PC, 내일)

.NET 8 SDK 필요 (현재 이 PC엔 런타임만 있음).

```powershell
# 수신측 — 포트는 기본 45871
dotnet publish src/Monitor.Receiver -c Release -o dist/receiver

# 전송측 — 수신측 IP를 여기서 굽는다
./scripts/build-sender.ps1 -ReceiverHost 192.168.0.50 -DeviceId SENDER-01
```

## 로컬 검증 (PC 한 대에서)

1. `dist/receiver/ScreenReceiver.exe` 실행 → 검은 창이 "waiting on port 45871"
2. `-ReceiverHost 127.0.0.1`로 전송측을 빌드해 실행 → 자기 화면이 창에 뜬다
3. 수신측 창 최소화 → 전송측 CPU가 0으로 떨어지는지 작업 관리자로 확인
4. 수신측 종료 후 재실행 → 전송측을 만지지 않아도 재접속

## 설치 (전송측 PC, 관리자 권한)

```powershell
./scripts/install-sender.ps1
```

`%LOCALAPPDATA%\ScreenSender\`에 복사하고 "로그온 시 실행" 작업을 등록한다. 스크립트가 끝에 안내하는 **수동 단계 3가지(자동 로그인, 화면보호기 잠금 해제, 세션 잠금 방지)** 를 반드시 수행해야 재부팅 후 무인 동작한다. 잠긴 세션은 검은 화면만 캡처된다.

## 아직 확인 안 된 것 (REQUIREMENTS.md §13)

- 폐쇄망에 하드웨어 방화벽/그룹 정책으로 outbound가 막혀 있으면 조용히 실패한다. 첫 연결로 판별.
- 마우스 커서는 프레임에 안 그려진다(§13.8). MVP 범위 밖.
