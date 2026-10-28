namespace DummyClient
{
    internal sealed record ClientOptions(string Host, int Port, Uri LoginUrl)
    {
        public static ClientOptions Parse(string[] args)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index += 2)
            {
                string option = args[index];
                if (option is not ("--host" or "--port" or "--login-url"))
                {
                    throw new ArgumentException("알 수 없는 옵션입니다.");
                }
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"{option} 뒤에 값이 필요합니다.");
                }
                if (!values.TryAdd(option, args[index + 1]))
                {
                    throw new ArgumentException($"{option}는 한 번만 지정합니다.");
                }
            }

            string host = values.GetValueOrDefault("--host", "127.0.0.1");
            if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('/'))
            {
                throw new ArgumentException("--host에는 호스트 이름이나 IP 주소가 필요합니다.");
            }
            if (!int.TryParse(values.GetValueOrDefault("--port", "7777"), out int port) || port is < 1 or > 65535)
            {
                throw new ArgumentException("--port에는 1~65535 포트 번호가 필요합니다.");
            }
            string loginUrl = values.GetValueOrDefault("--login-url", "http://127.0.0.1:5000/login");
            if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("--login-url에는 http 또는 https 주소가 필요합니다.");
            }
            return new ClientOptions(host, port, uri);
        }
    }
}
