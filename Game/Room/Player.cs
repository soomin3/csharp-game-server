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
        public float PendingX { get; set; }
        public float PendingY { get; set; }
        public bool Moved { get; set; }
        public HashSet<Player> KnownPlayers { get; private set; } = new HashSet<Player>();

        // 틱에서 새로 계산한 시야 집합으로 바꾸고, 이전 집합은 다음 관찰자의 계산에 다시 쓰도록 돌려준다.
        internal HashSet<Player> SwapKnownPlayers(HashSet<Player> visible)
        {
            HashSet<Player> previous = KnownPlayers;
            KnownPlayers = visible;
            return previous;
        }
    }
}
