using System.Net.Sockets;
using System.Text;
using Common.Network;

namespace Game.Network
{
    public interface IGameSession
    {
        string Name { get; set; }
        void SendPacket(byte[] packet);
    }
    public class GameSession : TSession, IGameSession
    {
        private readonly GameHandler _handler;

        public string Name { get; set; } = "(로그인 전)";

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
            switch (id)
            {
                case PacketId.Login:
                    _handler.ResLogin(this, Encoding.UTF8.GetString(buffer, offset, length));
                    break;
                case PacketId.Echo:
                    _handler.ResEcho(this, Encoding.UTF8.GetString(buffer, offset, length));
                    break;
                case PacketId.Chat:
                    _handler.ResChat(this, Encoding.UTF8.GetString(buffer, offset, length));
                    break;
                case PacketId.MoveReq:
                    {
                        MoveReq? req = MoveReq.ToValue(buffer, offset, length);
                        if (req == null)
                        {
                            Console.WriteLine("[GameSession] 잘못된 MoveReq — 무시");
                            break;
                        }

                        _handler.ResMove(this, req);
                        break;
                    }
                default:
                    Console.WriteLine($"[GameSession] 모르는 패킷 id={id}");
                    break;
            }
        }
    }
}