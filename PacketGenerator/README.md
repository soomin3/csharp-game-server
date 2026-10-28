# 패킷 생성기

솔루션 루트에서 실행한다.

```powershell
dotnet run --project PacketGenerator -- Protocol/packets.json .
dotnet build
dotnet test --no-build
```

`Protocol/packets.json`이 패킷 형식의 원본이다. 생성 결과는 저장소에 함께 커밋하며, 아래 폴더의 파일은 직접 고치지 않는다.

- `Common/Network/Generated`: 패킷 번호와 패킷 클래스
- `Game/Network/Generated`: 요청 핸들러 인터페이스와 분배기

정의에는 `name`, `id`, `direction`, `fields`를 적는다. 필드는 순서대로 바이트로 쓰고 읽으며, 지원하는 타입은 `string`, `int`, `float`이다. 문자열은 UTF-8 바이트 길이(int32)와 본문으로 쓴다. `BAReader`와 `BAWriter`, 프레임 헤더는 공통 수동 코드로 유지한다.

클라이언트 요청(`clientToServer`)의 클래스 이름은 `Req`, 서버 응답·알림(`serverToClient`)은 `Res` 또는 `Ntf`로 끝나야 한다. 이 규칙으로 `Array`, `Console` 같은 공통 코드의 타입 이름을 패킷 클래스가 가리는 것을 막는다. 필드가 `System`, `PacketId`, `writer`처럼 공통 코드와 같은 이름이어도 생성된 코드에서는 전체 타입 이름과 `this`를 사용해 구분한다.

```json
{
  "name": "ProbeReq",
  "id": 10,
  "direction": "clientToServer",
  "fields": [{ "name": "Value", "type": "int" }]
}
```

이 항목을 `packets` 배열에 추가하고 생성기를 실행하면 번호·클래스·분배기·인터페이스가 함께 만들어진다. 요청 핸들러 이름은 기본적으로 `ProbeReq`에서 `Req`를 뺀 뒤 `Res`를 붙인 `ResProbe`이다. `handler`로 이름을 직접 지정할 수도 있다.

`GameHandler`에 해당 메서드를 구현하면 처리가 연결된다. 아직 구현하지 않은 요청의 기본 동작은 연결 종료이므로, 새 정의를 추가한 상태에서도 빌드할 수 있다.

기존 Echo/Login/Chat처럼 요청과 응답이 같은 번호를 쓰면 양쪽에 같은 `idName`을 적고 `direction`을 `clientToServer`와 `serverToClient`로 나눈다. `idName`을 생략하면 클래스 이름을 번호 이름으로 쓴다. 같은 방향에서 번호를 중복해서 쓸 수 없다.

식별자·번호 범위·필드 중복·지원 타입·방향을 검증한 뒤 파일을 쓴다. 현재 프레임 형식은 본문이 없는 패킷을 허용하지 않으므로 빈 `fields`도 거절한다. 생성 결과는 패킷 항목의 나열 순서와 실행 시각에 따라 바뀌지 않으며, 내용이 같으면 파일을 다시 쓰지 않는다. 필드의 나열 순서는 실제 전송 순서이므로 변경하면 형식도 바뀐다. 없어진 정의의 파일은 생성기 표시가 있는 `.g.cs`에 한해서 두 생성 폴더 안에서 정리한다.

프로토콜 형식이 바뀌면 서버와 클라이언트를 함께 갱신한다.
