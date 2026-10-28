using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Common.Auth
{
    public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);
    public sealed record TokenIdentity(string Name, DateTimeOffset ExpiresAt);

    // A private, versioned HMAC token format. This is not a JWT or account authentication.
    public sealed class TokenService
    {
        public const string KeyEnvironmentVariable = "MY_SERVER_TOKEN_KEY";
        public const string DefaultIssuer = "MyServer.Login";
        public const int MaxTokenLength = 2048;
        private const string Version = "v1";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly byte[] _key;
        private readonly string _issuer;
        private readonly TimeSpan _lifetime;
        private readonly TimeProvider _timeProvider;

        private sealed record TokenPayload(string Issuer, string Name, long ExpiresAt);

        public TokenService(byte[] key, string issuer = DefaultIssuer, TimeProvider? timeProvider = null,
            TimeSpan? lifetime = null)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (key.Length < 32)
            {
                throw new ArgumentException("The signing key must contain at least 32 random bytes.", nameof(key));
            }
            if (!IsValidText(issuer, 128))
            {
                throw new ArgumentException("The issuer must contain 1 to 128 UTF-8 bytes without surrounding whitespace or control characters.", nameof(issuer));
            }

            _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
            if (_lifetime < TimeSpan.FromSeconds(1) || _lifetime > TimeSpan.FromHours(24))
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime), "Token lifetime must be between one second and 24 hours.");
            }

            _key = key.ToArray();
            _issuer = issuer;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public static TokenService FromEnvironment(TimeProvider? timeProvider = null,
            Func<string, string?>? readEnvironment = null)
        {
            string? encodedKey = (readEnvironment ?? Environment.GetEnvironmentVariable)(KeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(encodedKey) || encodedKey.Length > 4096)
            {
                throw new InvalidOperationException($"Set {KeyEnvironmentVariable} to a Base64-encoded key containing at least 32 random bytes before starting Login or Game.");
            }

            try
            {
                byte[] key = Convert.FromBase64String(encodedKey);
                try
                {
                    return new TokenService(key, timeProvider: timeProvider);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                throw new InvalidOperationException($"{KeyEnvironmentVariable} must be Base64 containing at least 32 random bytes.");
            }
        }

        public IssuedToken Issue(string name)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException("Name must contain 1 to 64 UTF-8 bytes without surrounding whitespace or control characters.", nameof(name));
            }

            long expiry = _timeProvider.GetUtcNow().Add(_lifetime).ToUnixTimeSeconds();
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(_issuer, name, expiry));
            string signedContent = Version + "." + Encode(payload);
            byte[] signature = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(signedContent));
            return new IssuedToken(signedContent + "." + Encode(signature), DateTimeOffset.FromUnixTimeSeconds(expiry));
        }

        public bool TryValidate(string? token, out TokenIdentity? identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            {
                return false;
            }

            string[] parts = token.Split('.');
            if (parts.Length != 3 || parts[0] != Version ||
                !TryDecode(parts[1], out byte[] payloadBytes) ||
                !TryDecode(parts[2], out byte[] signature) || signature.Length != 32)
            {
                return false;
            }

            byte[] expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            {
                return false;
            }

            try
            {
                TokenPayload? payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
                if (payload == null || !string.Equals(payload.Issuer, _issuer, StringComparison.Ordinal) ||
                    !IsValidName(payload.Name))
                {
                    return false;
                }

                DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
                if (expiresAt <= _timeProvider.GetUtcNow())
                {
                    return false;
                }

                identity = new TokenIdentity(payload.Name, expiresAt);
                return true;
            }
            catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        public static bool IsValidName(string? name) => IsValidText(name, 64);

        private static bool IsValidText(string? text, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Any(char.IsControl))
            {
                return false;
            }

            try
            {
                return StrictUtf8.GetByteCount(text) <= maxBytes;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        private static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool TryDecode(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text.Length == 0 || text.Length % 4 == 1 ||
                text.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            {
                return false;
            }

            try
            {
                string padded = text.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
                bytes = Convert.FromBase64String(padded);
                return Encode(bytes) == text;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
