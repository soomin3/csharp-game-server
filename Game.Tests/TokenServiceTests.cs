using System.Security.Cryptography;
using System.Text;
using Common.Auth;

namespace Game.Tests
{
    public class TokenServiceTests
    {
        private static byte[] Key() => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        [Fact]
        public void 발급한_토큰은_이름과_같은_만료시각을_복원한다()
        {
            ManualTimeProvider clock = new ManualTimeProvider();
            TokenService service = new TokenService(Key(), timeProvider: clock);

            IssuedToken issued = service.Issue("김철수");

            Assert.True(service.TryValidate(issued.Token, out TokenIdentity? identity));
            Assert.NotNull(identity);
            Assert.Equal("김철수", identity.Name);
            Assert.Equal(clock.GetUtcNow().AddMinutes(30), issued.ExpiresAt);
            Assert.Equal(issued.ExpiresAt, identity.ExpiresAt);
            Assert.True(issued.Token.Length <= TokenService.MaxTokenLength);
        }

        [Fact]
        public void 만료_직전에는_성공하고_정확한_만료시각부터_실패한다()
        {
            ManualTimeProvider clock = new ManualTimeProvider();
            TokenService service = new TokenService(Key(), timeProvider: clock);
            IssuedToken issued = service.Issue("Kim");

            clock.Now = issued.ExpiresAt.AddMilliseconds(-1);
            Assert.True(service.TryValidate(issued.Token, out _));
            clock.Now = issued.ExpiresAt;
            Assert.False(service.TryValidate(issued.Token, out _));
            clock.Now = issued.ExpiresAt.AddDays(1);
            Assert.False(service.TryValidate(issued.Token, out _));
        }

        [Fact]
        public void 이름을_변조하면_서명이_맞지_않는다()
        {
            TokenService service = new TokenService(Key());
            IssuedToken issued = service.Issue("Kim");
            string[] parts = issued.Token.Split('.');
            parts[1] = Encode(Encoding.UTF8.GetBytes("{\"Issuer\":\"MyServer.Login\",\"Name\":\"Admin\",\"ExpiresAt\":4102444800}"));

            Assert.False(service.TryValidate(string.Join('.', parts), out _));
        }

        [Fact]
        public void 서명을_변조하면_거절한다()
        {
            TokenService service = new TokenService(Key());
            string[] parts = service.Issue("Kim").Token.Split('.');
            parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];

            Assert.False(service.TryValidate(string.Join('.', parts), out _));
        }

        [Fact]
        public void 같은_키라도_다른_발급자_토큰은_거절한다()
        {
            TokenService issuer = new TokenService(Key(), issuer: "Another.Login");
            TokenService verifier = new TokenService(Key());

            Assert.False(verifier.TryValidate(issuer.Issue("Kim").Token, out _));
        }

        [Fact]
        public void 다른_키로_발급한_토큰은_거절한다()
        {
            TokenService issuer = new TokenService(Key());
            byte[] wrongKey = Key();
            wrongKey[0] ^= 0xff;
            TokenService verifier = new TokenService(wrongKey);

            Assert.False(verifier.TryValidate(issuer.Issue("Kim").Token, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("abc")]
        [InlineData("v1.a.b")]
        [InlineData("v2.e30.abc")]
        [InlineData("v1.e30=.abc")]
        [InlineData("v1.e30.abc.extra")]
        [InlineData("v1.한글.abc")]
        public void 빈_토큰과_잘못된_형식은_예외_없이_거절한다(string? token)
        {
            TokenService service = new TokenService(Key());

            Assert.False(service.TryValidate(token, out TokenIdentity? identity));
            Assert.Null(identity);
        }

        [Fact]
        public void 길이_한도를_넘은_토큰은_거절한다()
        {
            TokenService service = new TokenService(Key());
            Assert.False(service.TryValidate(new string('a', TokenService.MaxTokenLength + 1), out _));
        }

        [Theory]
        [InlineData("not JSON")]
        [InlineData("{}")]
        [InlineData("{\"Issuer\":\"MyServer.Login\",\"Name\":null,\"ExpiresAt\":4102444800}")]
        [InlineData("{\"Issuer\":\"MyServer.Login\",\"Name\":\"Kim\",\"ExpiresAt\":9223372036854775807}")]
        public void 서명이_맞아도_잘못된_내용은_예외_없이_거절한다(string payload)
        {
            TokenService service = new TokenService(Key());
            string content = "v1." + Encode(Encoding.UTF8.GetBytes(payload));
            string token = content + "." + Encode(HMACSHA256.HashData(Key(), Encoding.ASCII.GetBytes(content)));

            Assert.False(service.TryValidate(token, out _));
        }

        [Fact]
        public void 생성자에_넘긴_키_배열을_바꿔도_서비스_키는_변하지_않는다()
        {
            byte[] inputKey = Key();
            TokenService issuer = new TokenService(inputKey);
            TokenService verifier = new TokenService(Key());
            inputKey[0] ^= 0xff;

            Assert.True(verifier.TryValidate(issuer.Issue("Kim").Token, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-base64")]
        [InlineData("YWJj")]
        public void 키가_없거나_잘못되면_시작_시점에_실패한다(string? key)
        {
            Assert.Throws<InvalidOperationException>(() =>
                TokenService.FromEnvironment(readEnvironment: name => name == TokenService.KeyEnvironmentVariable ? key : null));
        }

        [Fact]
        public void 같은_환경변수_키로_만든_별도_서비스가_서로_검증한다()
        {
            string encodedKey = Convert.ToBase64String(Key());
            TokenService issuer = TokenService.FromEnvironment(readEnvironment: _ => encodedKey);
            TokenService verifier = TokenService.FromEnvironment(readEnvironment: _ => encodedKey);

            Assert.True(verifier.TryValidate(issuer.Issue("Kim").Token, out _));
        }

        [Fact]
        public void 이름_길이는_UTF8_바이트로_제한한다()
        {
            Assert.True(TokenService.IsValidName(new string('a', 64)));
            Assert.True(TokenService.IsValidName(new string('김', 21)));
            Assert.False(TokenService.IsValidName(new string('a', 65)));
            Assert.False(TokenService.IsValidName(new string('김', 22)));
            Assert.False(TokenService.IsValidName(" Kim"));
            Assert.False(TokenService.IsValidName("Kim\n"));
            Assert.False(TokenService.IsValidName("\ud800"));
        }

        private static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed class ManualTimeProvider : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
