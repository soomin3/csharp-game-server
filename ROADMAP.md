# MyServer 개발 현황과 계획

마지막 갱신: 2026-10-28

다시 이어서 할 때는 이 문서를 먼저 읽고, **"3. 지금 상태"**가 실제 코드와 맞는지 확인한 다음 시작한다.

---

## 1. 문서 작성 규칙

이 문서를 고칠 때 지키는 규칙이다.

1. **언제 고치나**
   - 한 단계가 끝났을 때. "끝났다"는 빌드 오류 0개와 테스트 통과를 확인했다는 뜻이다.
   - 계획(단계 순서나 범위)이 바뀌었을 때.
   - 오래 쉬기 전.
2. **상태는 실제로 확인한 것만 쓴다.** 빌드·테스트 결과와 확인한 날짜를 함께 적는다. "아마 될 것" 같은 추측은 쓰지 않는다.
3. **날짜는 `2026-10-28` 같은 절대 날짜로 쓴다.** "어제", "지난주"는 쓰지 않는다.
4. **단계 번호는 바꾸지 않는다.**
   - 중간에 끼워 넣을 일은 `12-1`처럼 번호를 붙인다.
   - 하지 않기로 한 단계는 지우지 말고 `취소 (이유)`로 표시한다.
5. **앞으로 할 단계는 모두 같은 칸으로 쓴다:** 목표 / 왜(다른 방식과 비교해서 이 방식을 채택 한 이유) / 범위(넣을 것·뺄 것) /   완료 기준 / 상태
6. **완료 기준은 확인할 수 있는 문장으로 쓴다.**
   - 좋은 예: "테스트 10개 통과", "클라 2개로 X를 하면 Y가 찍힘".
   - "잘 동작함" 같은 문장은 쓰지 않는다.
7. **끝난 단계는 "완료한 단계" 표의 한 줄로 줄인다.** 만든 것과 처음 배운 C#만 남긴다. 코드 전문은 남기지 않는다. 코드는 프로젝트 파일 자체가 기록이다.
8. **부록에는 "다음에 적용할 단계"의 코드만 둔다.** 적용이 끝나면 그 부록은 지운다.
9. **계획이 바뀌면** 맨 아래 "변경 기록"에 날짜와 이유를 한 줄 남긴다.
10. **쉬운 말로 쓴다.** 전문용어는 처음 나오는 자리에 괄호로 풀이를 붙인다.
11. **코드 위치는 줄 번호 대신 찾을 수 있는 말로 쓴다.** 예: "`HandleChat` 함수 바로 아래". 줄 번호는 코드를 고칠 때마다 바뀐다.

---

## 2. 진행 규칙 (한 단계를 진행하는 방식)

- 코드는 사용자가 직접 타이핑한다. 안내만 하고 MyServer 코드를 직접 고치지 않는다.
- 안내하기 전에 MyServer 복사본에서 빌드·테스트·실행까지 해보고, 실제로 나온 출력을 함께 알려준다.
  - 복사본 서버는 포트 **17777**로 바꿔서 돌린다. 사용자가 MyServer 서버(7777)를 띄워두고 있을 수 있기 때문이다.
- 모든 지시에는 "왜 하는지"를 같이 적는다. 가능하면 C++에서 대응하는 개념도 함께 적는다.
- 이미 있는 파일을 고칠 때는 **조각**으로 준다.
  - 맨 위에 적용 순서표를 둔다.
  - 순서는 **정의되는 쪽 → 그걸 쓰는 쪽**이다.
  - 각 조각은 "파일 + 위치 → 코드 → 설명" 순서다.
  - 중간중간 "여기까지 하고 빌드" 확인 지점을 둔다.
- 새 도구나 타입을 들일 때는 "이미 배운 것으로는 왜 안 되는지"를 먼저 설명한다. 이유가 약하면 이미 쓰는 것으로 통일한다.

---

## 3. 지금 상태

| 항목 | 상태 (2026-10-28 확인) |
|---|---|
| 완료 | **1~10단계** |
| 빌드 | 경고 0개, 오류 0개 |
| 테스트 | `dotnet test` 9개 통과 (GameHandlerTests 6, PacketTests 2, ConcurrencyTests 1) |
| 다음 할 일 | **11단계(틱) 적용.** 안내는 받았고 코드는 아직 안 들어감 → [부록 A](#부록-a-11단계-틱-적용-내용) |

---

## 4. 지금 구조

### 패킷 하나가 지나가는 길 (서버)

```
DummyClient ──TCP──► TSession 받기 루프 (RunAsync, 네트워크 스레드)
                       │ PacketFramer: 바이트를 패킷 단위로 자름
                       ▼
                     GameSession.OnPacket: 번호(PacketId)로 갈라서 패킷 내용을 풂
                       ▼
                     GameHandler.ResXxx: 방에 일감을 넣기만 함 (Push)
                       ▼
                     GameRoom 일감 줄 (Channel) ── room.RunAsync()가 한 번에 하나씩 꺼내 실행
                       ▼
                     GameRoom.HandleXxx: 게임 상태(명단 등)는 여기서만 만짐
                       ▼
                     session.SendPacket ──► TSession 보내기 줄 (Channel) ──► 보내기 루프 ──TCP──► 클라
```

### 파일별 역할

| 파일 | 하는 일 |
|---|---|
| `Common/Network/PacketHeader.cs` | 헤더 4바이트(길이 2 + 종류 2) 상수 |
| `Common/Network/PacketId.cs` | 패킷 번호: None 0, Echo 1, Login 2, Chat 3, MoveReq 4, MoveNtf 5 |
| `Common/Network/PacketFramer.cs` | 받은 바이트를 패킷 단위로 자르기 |
| `Common/Network/PacketBuilder.cs` | 문자열 패킷 만들기 (Login / Chat / Echo) |
| `Common/Network/BAWriter.cs` · `BAReader.cs` | 숫자·문자열을 바이트로 쓰고 읽기 (struct) |
| `Common/Network/MoveReq.cs` · `MoveNtf.cs` | 이동 요청 / 이동 알림 패킷 |
| `Common/Network/TServer.cs` | 접속 받기. 세션을 만드는 함수(Func)를 받아서 씀 |
| `Common/Network/TSession.cs` | 접속 하나를 맡음: 받기 루프 + 보내기 루프 (abstract) |
| `Common/Job/JobSerializer.cs` | 일감 줄 (Channel): Push / Flush / RunAsync |
| `Common/Network/EchoPacket.cs` | 쓰지 않음 (2단계 class/struct 실험용) |
| `Game/Program.cs` | 방·핸들러·서버를 만들어 조립 |
| `Game/Network/GameSession.cs` | TSession의 자식, 패킷 분배. **`IGameSession` 인터페이스도 이 파일 안에 있음** |
| `Game/Network/GameHandler.cs` | 패킷 종류별 함수. 방에 Push만 함 |
| `Game/Room/GameRoom.cs` | 게임 상태와 처리 (일감 줄을 통해서만 실행) |
| `DummyClient/Program.cs` | 이름 입력 후 채팅, `/move x y`로 이동 |
| `Game.Tests/FakeGameSession.cs` | 소켓 없는 가짜 세션 (보낸 패킷을 기록) |
| `Login/Program.cs` | 템플릿 그대로 (아직 안 씀) |

---

## 5. 개발 단계 전체

**끝 지점:** 접속 → 로그인 → 입장 → 이동·채팅 → 퇴장까지 전체 흐름이 돌아가고, 자동 더미 클라 수백 명으로 부하를 걸었을 때 틱 처리가 50ms 안에 끝나는 것을 확인한 상태.

단계 순서와 범위는 제안이다. 바꾸면 "변경 기록"에 남긴다.

### 묶음 A. 기초 — 완료

| 단계 | 만든 것 | 처음 배운 C# |
|---|---|---|
| 1. 뼈대 | 솔루션, 프로젝트 참조 | csproj/slnx, ProjectReference, ImplicitUsings, Nullable |
| 2. Common 첫 코드 | PacketId, PacketHeader(struct), EchoPacket | namespace, class와 struct(참조와 값), `enum : ushort`, 프로퍼티, const |
| 3. TCP 에코 | TServer, TSession | async/await, Task, `_ =`, try/catch, readonly, public과 internal |
| 4. 패킷 경계 | PacketFramer, PacketBuilder | static class, ref, Action, 버퍼 두 개(읽기용·조립용), 로컬 함수 |
| 5. 상속 | abstract TSession, GameSession, Func를 받는 TServer | abstract/override/protected, `: base(...)`, Func, 람다 |
| 6. 패킷 분배 | Login 패킷, switch 분배, GameHandler | switch(break 필수), enum은 범위 검사를 안 함, 람다 캡처 |
| 7. 테스트 | Game.Tests, IGameSession, FakeGameSession | interface, xUnit `[Fact]`·Assert, `=>` 함수, 스택 추적 읽기 |
| 8. 채팅 | 모두에게 보내기, TSession 보내기 루프(Channel), 클라 받기 루프 | Channel, `await foreach`, NetworkStream은 읽기 하나·쓰기 하나만 동시에 |
| 9. 일감 줄 | JobSerializer(Channel), GameRoom, 핸들러는 Push만, `room.RunAsync()` | List 동시 접근 문제, Parallel.For, out, 보장되는 건 "한 번에 하나씩" |
| 10. 숫자 패킷 | BAWriter/BAReader(struct), MoveReq/MoveNtf, `/move` | BinaryPrimitives, sizeof, Span, struct를 도우미 함수에 넘길 땐 ref, NaN 비교 |

### 묶음 B. 게임 서버의 기본 흐름

#### 11. 틱 (일정 간격으로 도는 게임 루프)
- **목표:** 방이 50ms마다 틱을 돈다. 이동 요청은 위치를 기록만 하고, 틱에서 움직인 사람의 마지막 위치를 한 번만 알린다.
- **왜:** 이동 요청을 받는 족족 모두에게 보내면 패킷이 폭발한다. 다른 사람에게 필요한 건 마지막 위치 하나뿐이다. 회복·AI처럼 시간이 흐르면서 일어나는 일도 일정한 박동이 필요하다.
- **범위:** 넣을 것은 `Player` 클래스(게임 속 존재), `Dictionary`로 된 명단, 틱 루프(`PeriodicTimer`가 `Push(Tick)`), `HandleMove`의 "기록만"으로의 변경. 뺄 것은 입장 알림과 시야(12, 14).
- **완료 기준:** 테스트 10개 통과. 클라에서 `/move`를 연달아 빠르게 보내면 다른 사람 화면에는 마지막 위치만 찍힌다.
- **상태:** 안내 완료, 적용 전 → [부록 A](#부록-a-11단계-틱-적용-내용)

#### 12. 입장·퇴장 알림
- **목표:** 새로 들어온 사람은 이미 있는 사람들의 위치를 받고, 나간 사람은 남은 사람들의 화면에서 사라진다.
- **왜:** 지금은 늦게 들어온 사람이 다른 사람이 움직이기 전까지 그 사람의 위치를 모른다. 11단계에서 서버가 위치를 기억하게 된 것을 처음 써먹는 단계다.
- **범위:** 넣을 것은 로그인 직후 새 사람에게 현재 위치 목록 보내기, 다른 사람들에게 새 사람 입장 알림, 퇴장 알림 패킷, 클라 표시. 뺄 것은 시야(14).
- **완료 기준:** 테스트로 "늦게 들어온 사람이 기존 위치를 받는다"와 "나가면 남은 사람에게 퇴장 알림이 간다"를 확인. 실제로 Kim이 움직인 뒤 Lee가 접속하면 Lee 화면에 Kim의 위치가 찍힌다.
- **상태:** 예정

#### 13. 세션 상태와 방어
- **목표:** 로그인 전에는 채팅·이동을 못 하게 하고, 이상한 패킷이 오면 연결을 끊는다.
- **왜:** 지금은 로그인 없이 채팅해도 "(로그인 전)" 이름으로 모두에게 가고, 잘못된 MoveReq는 로그만 남긴 채 연결을 유지한다. 서버는 클라를 믿으면 안 된다.
- **범위:** 넣을 것은 세션 상태(접속됨/로그인됨), TSession의 연결 끊기 함수, "잘못된 패킷·모르는 번호·로그인 전 요청"을 어떻게 처리할지 규칙. 뺄 것은 진짜 인증(18).
- **완료 기준:** 테스트로 "로그인 전 채팅은 아무에게도 안 간다"를 확인. 잘못된 패킷을 보내는 테스트용 클라로 접속하면 서버 로그에 끊김이 찍히고 그 클라의 연결이 닫힌다.
- **상태:** 예정

#### 14. 시야 (가까운 사람에게만 보내기)
- **목표:** 이동·채팅 알림을 근처에 있는 사람에게만 보낸다.
- **왜:** 한 맵에 수백 명이 있으면 모두에게 보내는 방식은 버티지 못한다.
- **범위:** 넣을 것은 맵을 칸으로 나누기(예: 10×10), 내 칸과 주변 칸에 있는 사람에게만 보내기, 칸을 넘을 때 보이기/사라지기 알림. 뺄 것은 정밀한 거리 계산.
- **완료 기준:** 테스트로 "먼 사람은 이동 알림을 못 받는다"와 "다가오면 보이고 멀어지면 사라진다"를 확인.
- **상태:** 예정

### 묶음 C. 구조 다듬기

#### 15. 패킷 형식 통일과 분배기 분리
- **목표:** 모든 패킷을 BAWriter/BAReader 방식으로 바꾸고, `GameSession.OnPacket`의 switch를 별도 클래스(분배기)로 뺀다.
- **왜:** 지금은 문자열 방식과 BAWriter 방식이 섞여 있다. 또 switch가 GameSession 안에 있어서 소켓 없이는 분배를 테스트할 수 없다.
- **범위:** 넣을 것은 Login/Chat 등의 패킷 클래스, PacketBuilder 삭제, 분배기 클래스와 그 테스트. 뺄 것은 자동 생성(16).
- **완료 기준:** `PacketBuilder`를 쓰는 곳이 0곳. "바이트를 넣으면 맞는 핸들러 함수가 불린다"는 분배기 테스트가 통과한다.
- **상태:** 예정

#### 16. 패킷 코드 자동 생성
- **목표:** 정의 파일에 패킷 이름과 필드 목록만 적으면 패킷 클래스와 분배기 코드가 만들어진다.
- **왜:** 패킷이 수십 개가 되면 손으로 쓰는 ToPacket/ToValue와 switch가 실수의 원인이 된다. 쓰는 순서와 읽는 순서가 어긋나는 실수가 대표적이다.
- **범위:** 넣을 것은 정의 파일 형식 정하기, 생성 도구(콘솔 프로젝트), 생성 결과를 둘 폴더. 뺄 것은 기존 패킷 외의 새 기능.
- **완료 기준:** 정의 파일에 새 패킷 한 줄을 추가하고 도구를 돌리면 빌드가 되고, 핸들러 함수만 채우면 동작한다.
- **상태:** 예정

#### 17. 방 여러 개와 워커 스레드
- **목표:** 방을 여러 개 두고 방 사이를 이동할 수 있게 한다. 워커 스레드 몇 개가 일감이 생긴 방을 처리한다.
- **왜:** 방 하나는 "한 번에 하나씩"이라 CPU 코어를 하나만 쓴다. 방을 나누면 방끼리는 동시에 처리할 수 있다.
- **범위:** 넣을 것은 방 목록, 방 이동, 워커와 준비된 방 줄. 뺄 것은 방 자동 생성·삭제.
- **완료 기준:** 같은 방 안에서 동시 실행이 0번이고(9단계에서 쓴 동시 실행 감지), 다른 방은 동시에 처리되는 것을 스레드 번호로 확인한다. 방 이동 테스트가 통과한다.
- **상태:** 예정

### 묶음 D. 서비스 형태

#### 18. 로그인 서버와 토큰
- **목표:** Login 프로젝트(HTTP 서버)가 로그인하면 토큰(접속 허가증)을 발급하고, Game은 로그인 패킷에 담긴 토큰을 검증한다.
- **왜:** 지금은 이름만 보내면 누구나 들어온다.
- **범위:** 넣을 것은 Login의 로그인 주소, 토큰 발급·검증, 클라 흐름(HTTP 로그인 → 게임 접속). 뺄 것은 DB(19).
- **완료 기준:** 토큰이 없거나 틀리면 게임 로그인이 거부되고, 맞으면 입장한다.
- **상태:** 예정

#### 19. DB 저장
- **목표:** 캐릭터(이름, 위치)를 저장하고 불러온다. 다시 접속하면 마지막 위치에서 시작한다.
- **왜:** 지금은 서버를 끄면 모든 게 사라진다.
- **범위:** 넣을 것은 DB 고르기(예: SQLite), 저장 시점(퇴장할 때), DB 작업은 게임 스레드 밖에서 하고 결과만 방에 Push하기. 뺄 것은 인벤토리 같은 다른 데이터.
- **완료 기준:** 서버를 재시작한 뒤 다시 접속해도 마지막 위치에서 시작한다.
- **상태:** 예정

#### 20. 안정성
- **목표:** 문제가 생겨도 서버가 버티고, 정상적으로 끌 수 있게 한다.
- **범위:** 넣을 것은 보내기 줄 크기 제한(보낸 걸 읽어가지 않는 클라 끊기), 서버 정상 종료(접속자 저장 후 종료), 클라가 서버 끊김을 알아채기, 파일 로그. 뺄 것은 서버 여러 대.
- **완료 기준:** 일부러 읽지 않는 클라를 붙이면 일정 크기를 넘는 순간 끊긴다. Ctrl+C를 누르면 저장 로그를 찍고 종료한다.
- **상태:** 예정

#### 21. 부하 테스트 (끝 지점)
- **목표:** 자동 더미 클라 수백 명이 접속해서 돌아다니게 하고, 틱 처리 시간과 보낸 패킷 수를 잰다.
- **왜:** 지금까지 만든 구조가 사람이 많을 때도 버티는지 숫자로 확인한다.
- **범위:** 넣을 것은 부하용 콘솔 프로젝트, 틱 처리 시간 측정 로그. 뺄 것은 서버 여러 대 분산.
- **완료 기준:** 정한 인원(예: 500명)에서 틱 처리 시간이 50ms 안에 끝난다.
- **상태:** 예정

---

## 6. 알고 있는 한계와 정리할 것

| 내용 | 다룰 단계 |
|---|---|
| `Common/Network/EchoPacket.cs`는 쓰지 않는다. 지워도 된다 | 아무 때나 |
| Echo 패킷은 서버 처리와 테스트에만 있고, 클라는 더 이상 보내지 않는다 | 15 |
| `Login` 프로젝트는 템플릿(Hello World) 그대로다 | 18 |
| 잘못된 MoveReq가 오면 로그만 남기고 연결은 유지한다 | 13 |
| 로그인 전에 보낸 채팅도 "(로그인 전)" 이름으로 모두에게 간다 | 13 |
| `Name`이 세션(연결 객체)에 있다. 게임 상태이니 `Player`로 옮길 후보다 | 11 이후 |
| 보내기 줄(`Channel.CreateUnbounded`)에 크기 제한이 없다 | 20 |
| 서버를 정상적으로 끄는 처리가 없다 (Ctrl+C로 끔) | 20 |
| 클라는 서버가 먼저 꺼져도 알아채지 못한다. 그 뒤 입력을 보내면 예외로 종료될 수 있다 | 20 |
| 패킷 형식이 두 가지 섞여 있다 (문자열 / BAWriter) | 15 |

---

## 7. 자주 쓰는 명령

```bash
cd E:/MyServer && dotnet build
```

```bash
cd E:/MyServer && dotnet test
```

```bash
cd E:/MyServer && dotnet run --project Game
```

```bash
cd E:/MyServer && dotnet run --project DummyClient
```

- 서버 포트: 7777
- 클라 명령: 아무 문장 = 채팅, `/move x y` = 이동(맵 0~100), 빈 줄 = 종료

---

## 부록 A. 11단계 (틱) 적용 내용

MyServer 복사본에서 빌드 경고 0개·오류 0개, 테스트 10개 통과, 실제 실행까지 확인한 코드다. `GameHandler`, `GameSession`, `DummyClient`는 바뀌지 않는다.

| 순서 | 파일 | 내용 | 확인 |
|---|---|---|---|
| ① | `Game/Room/Player.cs` | 신규 | 빌드 |
| ② | `Game/Room/GameRoom.cs` | ②-1 ~ ②-5 (다 하고 나서 빌드) | 빌드 |
| ③ | `Game/Program.cs` | 한 줄 추가 | 빌드 |
| ④ | `Game.Tests/GameHandlerTests.cs` | 기존 이동 테스트 2개에 한 줄씩 + 새 테스트 1개 | `dotnet test` 10개 |

### ① `Game/Room/Player.cs` (신규)

```csharp
using Game.Network;

namespace Game.Room
{
    public class Player
    {
        public Player(IGameSession session)
        {
            Session = session;
        }

        public IGameSession Session { get; }
        public float X { get; set; }
        public float Y { get; set; }
        public bool Moved { get; set; }
    }
}
```

세션은 **연결**(받기·보내기)이고, 플레이어는 **게임 속 존재**(위치)다. 게임 상태는 방이 쥐고 세션은 연결 일만 하게 나눈다. `Moved`는 "지난 틱 이후 움직였나" 표시다.

### ② `Game/Room/GameRoom.cs`

**②-1.** `MapSize` 줄 바로 아래에 추가
```csharp
private const int TickIntervalMs = 50;
```

**②-2.** 명단 필드와 `SessionCount` 두 줄 교체
```csharp
private readonly Dictionary<IGameSession, Player> _players = new Dictionary<IGameSession, Player>();
public int SessionCount => _players.Count;
```
`Dictionary`는 C++ `std::unordered_map`이다. 세션으로 그 사람의 플레이어를 바로 찾을 수 있다.

**②-3.** `SessionCount` 줄 바로 아래에 추가
```csharp
public async Task RunTickLoopAsync()
{
    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
    while (await timer.WaitForNextTickAsync())
    {
        Push(Tick);
    }
}

public void Tick()
{
    foreach (Player player in _players.Values)
    {
        if (!player.Moved)
        {
            continue;
        }

        player.Moved = false;
        Console.WriteLine($"[GameRoom] 틱: {player.Session.Name} 위치 알림 → ({player.X}, {player.Y})");
        byte[] packet = new MoveNtf { Name = player.Session.Name, X = player.X, Y = player.Y }.ToPacket();
        foreach (IGameSession other in _players.Keys)
        {
            other.SendPacket(packet);
        }
    }
}
```
- `PeriodicTimer`: 정해진 간격마다 `await`로 깨워주는 타이머다.
- `using`: 범위를 벗어나면 자동으로 정리한다. C++ RAII(소멸자에서 정리)와 같다.
- **`Tick()`을 바로 부르지 않고 `Push(Tick)`으로 줄에 넣는 게 핵심이다.** 타이머 루프는 일감 처리와 다른 흐름에서 돈다. 여기서 바로 부르면 Tick이 명단을 도는 동안 로그인이 명단에 추가하는 일이 동시에 일어날 수 있다(9단계 문제). 일감 하나로 넣으면 "한 번에 하나씩"이 지켜진다.

**②-4.** `_sessions`가 나오는 나머지 줄 바꾸기

| 함수 | 바꾸기 전 | 바꾼 후 |
|---|---|---|
| `HandleLogin` | `_sessions.Add(session);` | `_players[session] = new Player(session);` |
| `HandleChat` | `foreach (IGameSession other in _sessions)` | `foreach (IGameSession other in _players.Keys)` |
| `HandleLeave` | `_sessions.Remove(session);` | `_players.Remove(session);` |
| 로그 3곳 | `_sessions.Count` | `_players.Count` |

`_players[session] = ...`은 C++ `std::map`의 `operator[]`처럼, 없으면 넣고 있으면 덮어쓴다.

**②-5.** `HandleMove` 통째로 교체
```csharp
public void HandleMove(IGameSession session, MoveReq req)
{
    if (!IsInMap(req.X) || !IsInMap(req.Y))
    {
        Console.WriteLine($"[GameRoom] 이동 거부: {session.Name} → ({req.X}, {req.Y}) 맵 밖");
        return;
    }

    if (!_players.TryGetValue(session, out Player? player))
    {
        return;
    }

    player.X = req.X;
    player.Y = req.Y;
    player.Moved = true;
    Console.WriteLine($"[GameRoom] 이동 기록: {session.Name} → ({req.X}, {req.Y})");
}
```
이동 요청이 와도 바로 보내지 않는다. 기록하고 표시만 켜두면, 다음 틱이 보낸다.

빌드했을 때 `CS0103: '_sessions' 이름이 현재 컨텍스트에 없습니다`가 나오면 ②-4에서 못 바꾼 줄이 남은 것이다.

### ③ `Game/Program.cs`

`_ = room.RunAsync();` 바로 아래에 추가
```csharp
_ = room.RunTickLoopAsync();
```
이 줄을 빠뜨리면 이동은 기록만 되고 알림이 나가지 않는다. 채팅은 그대로 된다.

### ④ `Game.Tests/GameHandlerTests.cs`

**④-1.** `이동하면_모두에게_MoveNtf를_보낸다`와 `맵_밖으로는_이동할_수_없다` 두 테스트에서 `room.Flush();` 바로 아래에 추가
```csharp
room.Tick();
```
이제 알림은 틱이 보낸다. 첫 번째 테스트는 이 줄이 없으면 실패한다. 두 번째 테스트는 이 줄이 있어야 "틱까지 돌았는데도 안 보냈다"를 확인하는 의미 있는 테스트가 된다.

**④-2.** `IdOf` 바로 위에 추가
```csharp
[Fact]
public void 한_틱_안에_여러_번_이동하면_마지막_위치만_한_번_보낸다()
{
    GameRoom room = new GameRoom();
    GameHandler handler = new GameHandler(room);
    FakeGameSession kim = new FakeGameSession();
    handler.ResLogin(kim, "Kim");

    handler.ResMove(kim, new MoveReq { X = 1f, Y = 1f });
    handler.ResMove(kim, new MoveReq { X = 2f, Y = 2f });
    handler.ResMove(kim, new MoveReq { X = 3f, Y = 3f });
    room.Flush();
    Assert.Single(kim.SentPackets);

    room.Tick();
    Assert.Equal(2, kim.SentPackets.Count);
    MoveNtf? ntf = MoveNtf.ToValue(kim.SentPackets[1], PacketHeader.Size, kim.SentPackets[1].Length - PacketHeader.Size);
    Assert.NotNull(ntf);
    Assert.Equal(3f, ntf.X);
    Assert.Equal(3f, ntf.Y);

    room.Tick();
    Assert.Equal(2, kim.SentPackets.Count);
}
```
이 테스트는 세 구간을 확인한다. `Flush` 뒤에는 기록만 되어 있어서 환영 패킷 1개뿐이다. 첫 틱 뒤에는 마지막 위치 하나만 나간다. 두 번째 틱에서는 움직임이 없었으니 아무것도 나가지 않는다.

**실행 확인 (복사본에서 실제로 나온 서버 로그):** `/move 1 1`, `/move 2 2`, `/move 3 3`을 연달아 보냈을 때
```
[GameRoom] 이동 기록: Kim → (1, 1)
[GameRoom] 이동 기록: Kim → (2, 2)
[GameRoom] 이동 기록: Kim → (3, 3)
[GameRoom] 틱: Kim 위치 알림 → (3, 3)
```

---

## 변경 기록

- 2026-10-28: 처음 작성. 1~10단계 완료 상태와 11~21단계 계획, 문서 작성 규칙 정리.
