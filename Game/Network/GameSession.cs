using System.Net.Sockets;
using System.Text;
using Common.Network;

namespace Game.Network
{
    public enum GameSessionState
    {
        Connected,
        LoggedIn,
        Disconnected,
    }

    public interface IGameSession
    {
        string Name { get; set; }
        GameSessionState State { get; set; }
        bool IsDisconnected { get; }
        void SendPacket(byte[] packet);
        void Disconnect();
    }
    public class GameSession : TSession, IGameSession
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly GameHandler _handler;

        public string Name { get; set; } = "(로그인 전)";
        public GameSessionState State { get; set; } = GameSessionState.Connected;

        public GameSession(TcpClient client, GameHandler handler) : base(client)
        {
            _handler = handler;
        }

        protected override void OnSessionConnected()
        {
            Console.WriteLine("[GameSession] 접속됨");
        }

        protected override void OnSessionDisconnected()
        {
            Console.WriteLine($"[GameSession] 접속 끊김");
            _handler.OnDisconnect(this);
        }

        protected override void OnPacket(PacketId id, byte[] buffer, int offset, int length)
        {
            if (IsDisconnected)
            {
                return;
            }

            try
            {
                switch (id)
                {
                    case PacketId.Login:
                        _handler.ResLogin(this, StrictUtf8.GetString(buffer, offset, length));
                        break;
                    case PacketId.Echo:
                        _handler.ResEcho(this, StrictUtf8.GetString(buffer, offset, length));
                        break;
                    case PacketId.Chat:
                        _handler.ResChat(this, StrictUtf8.GetString(buffer, offset, length));
                        break;
                    case PacketId.MoveReq:
                        {
                            MoveReq? req = MoveReq.ToValue(buffer, offset, length);
                            if (req == null || !float.IsFinite(req.X) || !float.IsFinite(req.Y))
                            {
                                RejectPacket("잘못된 MoveReq");
                                break;
                            }

                            _handler.ResMove(this, req);
                            break;
                        }
                    default:
                        RejectPacket($"허용하지 않는 패킷 id={(ushort)id}");
                        break;
                }
            }
            catch (DecoderFallbackException)
            {
                RejectPacket("잘못된 UTF-8 문자열");
            }
        }

        private void RejectPacket(string reason)
        {
            Console.WriteLine($"[GameSession] {reason} — 연결을 끊습니다.");
            Disconnect();
        }
    }
}
